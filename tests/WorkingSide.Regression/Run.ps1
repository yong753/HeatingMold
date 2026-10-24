$ErrorActionPreference = 'Stop'
$verifyScript = Join-Path $PSScriptRoot '../../scripts/Verify.ps1'

& $verifyScript -Group WorkingSide @args
