@{
    RootModule           = 'BlinkyLite.PowerShell.dll'
    ModuleVersion        = '0.1.0'
    GUID                 = '5c0b7f4e-2d7a-4c63-9f0e-6b1a8f3e9d21'
    Author               = 'Szymon Frankiewicz'
    CompanyName          = 'BlinkyLite contributors'
    Copyright            = 'Copyright 2026 Szymon Frankiewicz'
    Description          = 'Issue and verify YubiKey PIV credentials from Microsoft ADCS (Enroll On Behalf Of).'
    # The module is built on .NET 10; Windows PowerShell 5.1 and pwsh before
    # 7.6 run older runtimes and cannot load it. Saying so here turns a
    # confusing assembly-load error into a clear refusal at Import-Module.
    PowerShellVersion    = '7.6'
    CompatiblePSEditions = @('Core')
    CmdletsToExport      = @(
        'Connect-BlinkyLite',
        'Disconnect-BlinkyLite',
        'Get-BlinkyLiteCard',
        'Get-BlinkyLiteProfile',
        'Find-BlinkyLiteUser',
        'New-BlinkyLiteIssuance'
    )
    FunctionsToExport    = @()
    AliasesToExport      = @()
    VariablesToExport    = @()
    PrivateData          = @{
        PSData = @{
            Tags = @('YubiKey', 'PIV', 'ADCS', 'SmartCard')
        }
    }
}
