"""
WinCleaner Cloud Engine Rules Vault.
This proprietary ruleset is stored exclusively on the server and delivered
only to authenticated, paid, signed client sessions via AES-256 encryption.
"""

CLOUD_RULES_VERSION = "2.1.0-cloud"

MASTER_CLOUD_RULES = {
    "version": CLOUD_RULES_VERSION,
    "registry_clean": [
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", "action": "clear_subkeys"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", "action": "clear_values"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", "action": "clear_subkeys"},
        {"root": "HKCU", "path": r"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags", "action": "delete_tree"},
        {"root": "HKCU", "path": r"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU", "action": "delete_tree"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\Shell\Bags", "action": "delete_tree"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\Shell\BagMRU", "action": "delete_tree"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", "action": "clear_values"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "action": "clear_values"},
        {"root": "HKLM", "path": r"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache", "action": "clear_binary"},
        {"root": "HKLM", "path": r"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings", "action": "clear_subkeys"},
        {"root": "HKCU", "path": r"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "action": "clear_values"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView", "action": "delete_tree"},
        {"root": "HKCU", "path": r"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched", "action": "delete_tree"}
    ],
    "telemetry_rules": [
        {"root": "HKLM", "path": r"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "name": "AllowTelemetry", "type": "DWORD", "value": 0},
        {"root": "HKLM", "path": r"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "name": "MaxTelemetryAllowed", "type": "DWORD", "value": 0},
        {"root": "HKLM", "path": r"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "name": "DisabledByGroupPolicy", "type": "DWORD", "value": 1},
        {"root": "HKLM", "path": r"SOFTWARE\Policies\Microsoft\Windows\EnhancedStorageDevices", "name": "TCGSecurityActivationDisabled", "type": "DWORD", "value": 0},
        {"root": "HKCU", "path": r"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", "name": "TailoredExperiencesWithDiagnosticDataEnabled", "type": "DWORD", "value": 0},
        {"root": "HKLM", "path": r"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "name": "Disabled", "type": "DWORD", "value": 1},
        {"root": "HKLM", "path": r"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "name": "DoReport", "type": "DWORD", "value": 0}
    ],
    "telemetry_services": [
        "DiagTrack",
        "dmwappushservice",
        "diagnosticshub.standardcollector.service",
        "WerSvc"
    ],
    "network_tweaks": [
        {"root": "HKLM", "path": r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "name": "NetworkThrottlingIndex", "type": "DWORD", "value": 0xFFFFFFFF},
        {"root": "HKLM", "path": r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "name": "SystemResponsiveness", "type": "DWORD", "value": 0},
        {"root": "HKLM", "path": r"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "name": "DefaultTTL", "type": "DWORD", "value": 64},
        {"root": "HKLM", "path": r"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "name": "EnableTCPA", "type": "DWORD", "value": 1}
    ],
    "system_paths": [
        {"env": "SystemRoot", "subpath": "Prefetch", "pattern": "*.pf"},
        {"env": "LOCALAPPDATA", "subpath": r"Microsoft\Windows\Explorer", "pattern": "thumbcache_*.db"},
        {"env": "LOCALAPPDATA", "subpath": "D3DSCache", "pattern": "*.*"},
        {"env": "LOCALAPPDATA", "subpath": r"NVIDIA\GLCache", "pattern": "*.*"},
        {"env": "LOCALAPPDATA", "subpath": r"AMD\DxCache", "pattern": "*.*"},
        {"env": "SystemRoot", "subpath": r"SoftwareDistribution\Download", "pattern": "*.*"},
        {"env": "LOCALAPPDATA", "subpath": r"Microsoft\Windows\DeliveryOptimization", "pattern": "*.*"}
    ],
    "browser_targets": [
        {"name": "Google Chrome", "env": "LOCALAPPDATA", "root": r"Google\Chrome\User Data", "targets": [r"Default\Cache", r"Default\Code Cache", r"Default\GPUCache", r"ShaderCache"]},
        {"name": "Microsoft Edge", "env": "LOCALAPPDATA", "root": r"Microsoft\Edge\User Data", "targets": [r"Default\Cache", r"Default\Code Cache", r"Default\GPUCache", r"ShaderCache"]},
        {"name": "Brave Browser", "env": "LOCALAPPDATA", "root": r"BraveSoftware\Brave-Browser\User Data", "targets": [r"Default\Cache", r"Default\Code Cache", r"Default\GPUCache"]},
        {"name": "Yandex Browser", "env": "LOCALAPPDATA", "root": r"Yandex\YandexBrowser\User Data", "targets": [r"Default\Cache", r"Default\Code Cache", r"Default\GPUCache"]},
        {"name": "Opera", "env": "APPDATA", "root": r"Opera Software\Opera Stable", "targets": ["Cache", "GPUCache", "ShaderCache"]},
        {"name": "Opera GX", "env": "APPDATA", "root": r"Opera Software\Opera GX Stable", "targets": ["Cache", "GPUCache", "ShaderCache"]},
        {"name": "Mozilla Firefox", "env": "LOCALAPPDATA", "root": r"Mozilla\Firefox\Profiles", "targets": [r"cache2\entries", r"startupCache"]}
    ]
}
