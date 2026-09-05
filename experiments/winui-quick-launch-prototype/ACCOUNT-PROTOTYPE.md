# Account and About preview

Question: does a Mac-inspired identity header with separate license and update cards fit the approved Windows settings design?

New artifact: `PrototypeAccountView.cs`. Integration: settings catalog and window, MainWindow, App startup `--account`. Open Settings > Account & about, or launch the isolated prototype with `--account` after its usual Debug win-x64 build.

Reuses Inter, existing semantic brushes, icon-based choice fields and the animated setup logo (including its existing reduced-motion behavior). References: Mac AboutSettingsView and LicenseSettingsView. No pricing, entitlement promises or real version claims are copied.

Session-only scenarios: inactive/active/unavailable license; stable/preview channel; current/available/offline simulated update results. No keys, accounts, purchases, activation, updater requests or installations. Not a production licensing implementation.

Validation: Debug build passed without warnings/errors; native 1040×780 rendering inspected. Added explicit demo labels after visual inspection. Automated dropdown click did not visibly open the menu, so interaction coverage is incomplete; keyboard, narrow-window and all scenario transitions still need manual verification. Existing shared picker handles popup behavior and is registered with settings dismissal handling.

Ready for visual feedback, not production promotion.
