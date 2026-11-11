"""Writes model.dml.onnx next to the onnx-community Nemotron export for the DirectML engine.

The export uses ONNX Gelu (opset 20), which ONNX Runtime's DirectML provider does not implement:
all 31 Gelu nodes fall back to the CPU, with a GPU-to-CPU copy and back around each of them in every
chunk step. com.microsoft Gelu computes the same exact (erf) Gelu and runs on DirectML. Only the
graph changes; the weights stay in the shared model.onnx_data.

Usage: python -I dml_gelu.py <dir with model.onnx>
"""

import os
import sys

import onnx


def main() -> None:
    directory = sys.argv[1]
    source = os.path.join(directory, "model.onnx")
    target = os.path.join(directory, "model.dml.onnx")
    model = onnx.load(source, load_external_data=False)
    if not any(o.domain == "com.microsoft" for o in model.opset_import):
        model.opset_import.append(onnx.helper.make_opsetid("com.microsoft", 1))

    replaced = 0
    for node in model.graph.node:
        if node.op_type != "Gelu" or node.domain not in ("", "ai.onnx"):
            continue
        approximate = next((onnx.helper.get_attribute_value(a) for a in node.attribute if a.name == "approximate"), b"none")
        if approximate not in (b"none", "none"):
            raise SystemExit(f"{node.name}: approximate={approximate!r} has no com.microsoft equivalent here")
        node.domain = "com.microsoft"
        del node.attribute[:]
        replaced += 1

    # The tensors keep their references to model.onnx_data, so the new graph sits in the same directory.
    onnx.save(model, target)
    print(f"replaced {replaced} Gelu nodes: {target}")


if __name__ == "__main__":
    main()
