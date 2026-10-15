"""Keeps the Group Policy templates in deployment/windows in step with the policy schema in Core.

Every key in ManagedPolicyKeys.All must be covered by a machine policy that writes the documented registry value
(or list subkey plus <Name>Configured marker), recommendation-capable keys must also exist below the Recommended
subkey, and every string or presentation the ADMX references must exist in each ADML.
"""

import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEPLOYMENT = ROOT / "deployment" / "windows"
SCHEMA = ROOT / "src" / "TypeWhisper.Core" / "Models" / "ManagedPolicy.cs"
NS = {"p": "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions"}
ENFORCED_KEY = r"Software\Policies\TypeWhisper"
RECOMMENDED_KEY = ENFORCED_KEY + r"\Recommended"
LANGUAGES = ("en-US", "de-DE")


def schema_keys():
    """(name, kind, allows_recommendation, enumeration_maximum) for every definition in ManagedPolicyKeys.All."""
    text = SCHEMA.read_text(encoding="utf-8")
    block = text[text.index("All { get; } ="):]
    block = block[: block.index("];")]
    entries = []
    for match in re.finditer(r"new\((\w+), ManagedPolicyValueKind\.(\w+)([^)]*)\)", block):
        name, kind, rest = match.groups()
        maximum = re.search(r"EnumerationMaximum:\s*(\d+)", rest)
        entries.append((name, kind, "AllowsRecommendation: true" in rest, int(maximum.group(1)) if maximum else 0))
    return entries


def policies():
    tree = ET.parse(DEPLOYMENT / "TypeWhisper.admx")
    return tree.getroot(), tree.getroot().findall("./p:policies/p:policy", NS)


def references(root, prefix):
    pattern = re.compile(r"\$\(" + prefix + r"\.(\w+)\)")
    found = set()
    for element in root.iter():
        for value in element.attrib.values():
            found.update(pattern.findall(value))
    return found


def adml(language):
    tree = ET.parse(DEPLOYMENT / language / "TypeWhisper.adml")
    root = tree.getroot()
    strings = {e.get("id"): (e.text or "") for e in root.findall(".//p:stringTable/p:string", NS)}
    presentations = {e.get("id"): e for e in root.findall(".//p:presentationTable/p:presentation", NS)}
    return strings, presentations


class AdmxTemplateTests(unittest.TestCase):
    def test_schema_is_read(self):
        keys = schema_keys()
        self.assertGreaterEqual(len(keys), 18)
        self.assertIn(("ManagedLicenseKey", "Text", False, 0), keys)
        self.assertIn(("UpdateMode", "Enumeration", False, 2), keys)

    def test_every_key_has_a_machine_policy_with_the_documented_registry_shape(self):
        root, policy_list = policies()
        by_scope = {ENFORCED_KEY: {}, RECOMMENDED_KEY: {}}
        for policy in policy_list:
            self.assertEqual("Machine", policy.get("class"), policy.get("name"))
            self.assertIsNotNone(policy.find("./p:supportedOn", NS), policy.get("name"))
            key = policy.get("key")
            self.assertIn(key, by_scope, policy.get("name"))
            scope = by_scope[key]
            if policy.get("valueName"):
                scope[policy.get("valueName")] = ("policy", policy)
            for element in policy.findall("./p:elements/*", NS):
                tag = element.tag.split("}")[1]
                if tag == "list":
                    list_key = element.get("key")
                    self.assertTrue(list_key.startswith(key + "\\"), policy.get("name"))
                    scope[list_key[len(key) + 1:]] = ("list", element)
                else:
                    scope[element.get("valueName")] = (tag, element)
        for name, kind, allows_recommendation, maximum in schema_keys():
            with self.subTest(key=name):
                self.assertIn(name, by_scope[ENFORCED_KEY], "enforced policy missing")
                shape, element = by_scope[ENFORCED_KEY][name]
                if kind == "Boolean":
                    self.assertEqual("policy", shape)
                    self.assertEqual("1", element.find("./p:enabledValue/p:decimal", NS).get("value"))
                    self.assertEqual("0", element.find("./p:disabledValue/p:decimal", NS).get("value"))
                elif kind == "Enumeration":
                    self.assertEqual("enum", shape)
                    values = [item.find("./p:value/p:decimal", NS).get("value") for item in element.findall("./p:item", NS)]
                    self.assertEqual([str(i) for i in range(maximum + 1)], values)
                    self.assertEqual("true", element.get("required"))
                elif kind == "Text":
                    self.assertEqual("text", shape)
                    self.assertEqual("true", element.get("required"))
                    self.assertEqual("1024", element.get("maxLength"))
                else:
                    self.assertEqual("list", shape)
                    self.assertEqual("", element.get("valuePrefix"), "numbered value names need an empty valuePrefix")
                    marker_shape, marker = by_scope[ENFORCED_KEY][name + "Configured"]
                    self.assertEqual("policy", marker_shape)
                    self.assertEqual("1", marker.find("./p:enabledValue/p:decimal", NS).get("value"))
                    self.assertEqual("0", marker.find("./p:disabledValue/p:decimal", NS).get("value"))
                    self.assertIs(marker.find("./p:elements/p:list", NS), element)
                if allows_recommendation:
                    self.assertIn(name, by_scope[RECOMMENDED_KEY], "recommended policy missing")
                    self.assertEqual(shape, by_scope[RECOMMENDED_KEY][name][0])
                else:
                    self.assertNotIn(name, by_scope[RECOMMENDED_KEY], "restrictions cannot be recommended")
        extra = set(by_scope[ENFORCED_KEY]) - {name for name, *_ in schema_keys()} - {name + "Configured" for name, *_ in schema_keys()}
        self.assertEqual(set(), extra, "the template writes values the app does not read")

    def test_pairs_are_written_together(self):
        _, policy_list = policies()
        for name, first, second in (("TranscriptionSelection", "TranscriptionProviderId", "TranscriptionModelId"),
                                    ("LlmSelection", "LlmProviderId", "LlmModelId")):
            for suffix in ("", "_Recommended"):
                policy = next(p for p in policy_list if p.get("name") == name + suffix)
                names = [e.get("valueName") for e in policy.findall("./p:elements/p:text", NS)]
                self.assertEqual([first, second], names, policy.get("name"))

    def test_every_language_resolves_all_references(self):
        root, policy_list = policies()
        strings_used = references(root, "string")
        presentations_used = references(root, "presentation")
        element_ids = {e.get("id") for p in policy_list for e in p.findall("./p:elements/*", NS)}
        for language in LANGUAGES:
            with self.subTest(language=language):
                strings, presentations = adml(language)
                self.assertEqual(set(), strings_used - set(strings), "missing strings")
                self.assertEqual(set(), set(strings) - strings_used, "unused strings")
                self.assertEqual(set(), presentations_used - set(presentations), "missing presentations")
                self.assertEqual(set(), set(presentations) - presentations_used, "unused presentations")
                for text in strings.values():
                    self.assertTrue(text.strip(), "empty string")
                for presentation in presentations.values():
                    for control in presentation:
                        self.assertIn(control.get("refId"), element_ids, control.get("refId"))

    def test_supported_on_is_self_contained(self):
        root, policy_list = policies()
        definitions = {d.get("name") for d in root.findall("./p:supportedOn/p:definitions/p:definition", NS)}
        self.assertEqual({"SUPPORTED_TypeWhisper_1_2"}, definitions)
        self.assertEqual([], root.findall("./p:policyNamespaces/p:using", NS), "no dependency on other templates")
        for policy in policy_list:
            self.assertIn(policy.find("./p:supportedOn", NS).get("ref"), definitions)


if __name__ == "__main__":
    unittest.main()
