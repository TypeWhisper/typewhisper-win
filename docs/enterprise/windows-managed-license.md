# Managed license activation on Windows (TypeWhisper 1.2 and later)

TypeWhisper for Windows can read a commercial license key from machine policy and
activate it in the signed-in user's session. Administrators do not need to call
Polar or hand the key to users. This is the Windows counterpart of the
[macOS managed license guide](https://github.com/TypeWhisper/typewhisper-mac/blob/main/docs/mdm/README.md).
It is not available in 1.1.

## Registry value

| Value | Type | Key | Behavior |
| --- | --- | --- | --- |
| `ManagedLicenseKey` | `REG_SZ` | `HKLM\SOFTWARE\Policies\TypeWhisper` | Activates a commercial license automatically and manages its app controls while nonempty. |

The app reads the native 64-bit registry view. Surrounding whitespace is ignored and
an empty value counts as absent. Supporter keys are not accepted. The app never
writes, changes or removes the value.

TypeWhisper installs per user. On a shared device each Windows user runs the
installer once, or the deployment tool installs the app in that user's context;
the policy value itself is device-wide and applies to every user.

Example for an elevated PowerShell session:

```powershell
New-Item -Path 'HKLM:\SOFTWARE\Policies\TypeWhisper' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\TypeWhisper' -Name 'ManagedLicenseKey' -Value 'YOUR_LICENSE_KEY' -Type String
```

Deploy the value with Group Policy Preferences (a registry item), an Intune
PowerShell script that runs in the system context, or any other tool that writes
`HKLM`. ADMX templates for Group Policy and Intune follow with the managed settings
work in [#470](https://github.com/TypeWhisper/typewhisper-win/issues/470). Avoid
putting real keys in interactive shell history or script logs.

## Activation lifecycle

- The value is read at startup, when the policy changes while the app is running,
  and through **Retry activation** on Settings > Premium > License.
- The app stores the key and its Polar activation ID in the signed-in user's
  license file, encrypted with Windows Data Protection. It reuses the record across
  launches and does not activate on every launch.
- Each Windows user account on a device holds its own activation. A device with
  several users uses one seat per user, as macOS does with per-user Keychain records.
  The direct download and the Microsoft Store build keep separate profiles, so a user
  who runs both side by side takes one seat per build. Polar labels each activation
  with the machine name and the Windows user name, for example
  `TYPEWHISPER (tw-user2)`, so the seats of one device can be told apart.
- Existing licenses keep their normal offline validation behavior. An initial
  activation needs internet access; failures can be retried from the License page or
  on the next launch. No background retry loop is installed.
- Changing the key activates and stores the replacement before the previous
  activation is deactivated. A failed replacement leaves the previous license intact.
  If deactivating the previous activation fails, it stays listed in Polar and can be
  removed there.
- Removing the value restores the manual license controls on the next launch or
  retry. It does not revoke or deactivate the existing activation. Deactivate the
  device manually or revoke the license centrally in Polar; central changes are
  noticed on the normal validation schedule (up to seven days for an active license).
- A key that Polar rejects is not retried automatically. An activation deleted in
  Polar is recreated on the next check if the configured key remains valid.
- Managed mode hides key entry, device deactivation and purchase links, suppresses
  Premium purchase prompts, and shows the license status without the key. Supporter
  keys cannot be entered while a managed key is configured. Microphone access,
  model setup and the setup wizard are unchanged.

## Key confidentiality

This removes the need to distribute the key to employees manually. Registry
policies are not secret stores: any user on the device can read `HKLM` policy
values, and the app does not change that. Do not claim that a shared key is
inaccessible on managed devices. Use the deployment tool's access controls and
avoid logging policy contents. The key does not appear in TypeWhisper logs,
support reports, backups or exports; support reports list the value only as set.

## Pilot checklist

Before a fleet rollout, test the signed release on one managed device:

1. Deploy the value and launch TypeWhisper in a standard user's session. Confirm
   Settings > Premium > License shows the managed state and an active license.
2. Restart twice and confirm no additional Polar activations were created.
3. Repeat with another Windows user on the same device and test an initially
   offline launch followed by **Retry activation**.
4. Test key replacement and value removal according to the lifecycle above.
5. Confirm microphone consent and model provisioning for your intended setup.

Automated tests cover the activation lifecycle with mocked Polar responses. They do
not prove policy delivery through your deployment tool.
