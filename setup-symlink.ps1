# setup-symlink.ps1
# Creates a symlink from this repo's AddOn to NinjaTrader's custom AddOns folder
# Run as Administrator (or enable Developer Mode in Windows Settings)

$ErrorActionPreference = "Stop"

$source = "$PSScriptRoot\AddOns\NRDToCSV.cs"
$ntFolder = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom\AddOns"
$target = "$ntFolder\NRDToCSV.cs"

Write-Host "NRDToCSV Symlink Setup" -ForegroundColor Cyan
Write-Host "======================" -ForegroundColor Cyan
Write-Host ""

# Check if source file exists
if (-not (Test-Path $source)) {
    Write-Error "Source file not found: $source"
    exit 1
}
Write-Host "Source: $source" -ForegroundColor Gray

# Check if NinjaTrader folder exists
if (-not (Test-Path $ntFolder)) {
    Write-Error "NinjaTrader AddOns folder not found: $ntFolder"
    Write-Host "Make sure NinjaTrader 8 is installed and has been run at least once."
    exit 1
}
Write-Host "Target: $target" -ForegroundColor Gray
Write-Host ""

# Handle existing file
if (Test-Path $target) {
    $item = Get-Item $target

    if ($item.LinkType -eq "SymbolicLink") {
        $currentTarget = $item.Target
        if ($currentTarget -eq $source) {
            Write-Host "Symlink already exists and points to correct location." -ForegroundColor Green
            Write-Host "Nothing to do."
            exit 0
        }
        Write-Host "Removing existing symlink (pointed to: $currentTarget)..." -ForegroundColor Yellow
        Remove-Item $target
    } else {
        # It's a regular file - back it up with .bak extension so NinjaTrader won't compile it
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backup = "$target.$timestamp.bak"
        Write-Host "Existing file found. Creating backup..." -ForegroundColor Yellow
        Write-Host "(Using .bak extension to prevent NinjaTrader from compiling both files)" -ForegroundColor Gray
        Move-Item $target $backup
        Write-Host "Backed up to: $backup" -ForegroundColor Gray
    }
    Write-Host ""
}

# Create symlink
try {
    New-Item -ItemType SymbolicLink -Path $target -Target $source | Out-Null
    Write-Host "Symlink created successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "1. Open NinjaTrader 8"
    Write-Host "2. Go to Tools > NinjaScript Editor"
    Write-Host "3. Press F5 or click Compile to compile the AddOn"
    Write-Host "4. Access via Tools > NRD to CSV"
} catch {
    if ($_.Exception.Message -match "privilege") {
        Write-Host ""
        Write-Error "Failed to create symlink - Administrator privileges required!"
        Write-Host ""
        Write-Host "Options:" -ForegroundColor Yellow
        Write-Host "1. Run PowerShell as Administrator and try again"
        Write-Host "2. Enable Developer Mode: Settings > Privacy & Security > For developers > Developer Mode"
        Write-Host ""
    } else {
        throw
    }
    exit 1
}
