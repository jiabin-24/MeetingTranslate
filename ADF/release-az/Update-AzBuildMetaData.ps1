#requires -Module Az.Storage
#requires -Module Az.Accounts

<#
.SYNOPSIS
    Stage Build metadata from a Build on Azure Storage (Blob Container), for Deployment
.DESCRIPTION
    Stage Build metadata from a Build on Azure Storage (Blob Container), for Deployment. Primarily used for a PULL mode deployment where a Server can retrieve new builds via Desired State Configuration.
.EXAMPLE
    Update-AzBuildMetaData.ps1 -ComponentName $(ComponentName) -BuildName $(Build.BuildNumber) -BasePath "$(System.ArtifactsDirectory)/_$(ComponentName)/$(ComponentName)"

    As seen in an Azure DevOps pipeline
.INPUTS
    Inputs (if any)
.OUTPUTS
    Output (if any)
.NOTES
    Updated 04/03/2021 
        - Moved the script from using Storage File Shares to Storage Blob Containers

#>

Param (
    [String]$BuildName = '4.2',
    [String]$ComponentName = 'EchoBot',
    [String]$BasePath = 'D:\Builds',
    [String]$Environment = 'D1',
    [String]$MetaDataFileName = 'componentBuild.json',
    [String]$App = 'BOT',
    [Parameter(Mandatory)]
    [String]$Location,
    [Parameter(Mandatory)]
    [String]$OrgName
)

$LocationLookup = Get-Content -Path (Join-Path $PSScriptRoot '..\bicep\global\region.json') | ConvertFrom-Json
$Prefix = $LocationLookup.$Location.Prefix

# Azure Blob Container Info
[String]$SAName = "${Prefix}${OrgName}${App}${Environment}saglobal".tolower()
[String]$ContainerName = 'builds'

# Get context using Oauth
$Context = New-AzStorageContext -StorageAccountName $SAName -UseConnectedAccount

$StorageContainerParams = @{
    Container = $ContainerName
    Context   = $Context
}

Get-AzStorageBlob @StorageContainerParams -Blob "$ComponentName/$MetaDataFileName" | Format-Table -AutoSize
$destPath = Join-Path -Path $BasePath -ChildPath $ComponentName
if (-not (Test-Path -Path $destPath)) { New-Item -ItemType Directory -Path $destPath -Force | Out-Null }
Get-AzStorageBlobContent -Force @StorageContainerParams -Blob "$ComponentName/$MetaDataFileName" -Destination (Join-Path $destPath $MetaDataFileName) -Verbose

 $data = Get-Content -Path (Join-Path $destPath $MetaDataFileName) | ConvertFrom-Json
Write-Verbose -Message "Previous Build in [$environment] was [$($data.ComponentName.$ComponentName.$Environment.DefaultBuild)]" -Verbose
Write-Verbose -Message "Current  Build in [$environment] is  [$BuildName]" -Verbose
$data.ComponentName.$ComponentName.$Environment.DefaultBuild = $BuildName


$outFile = Join-Path $destPath $MetaDataFileName
$data | ConvertTo-Json -Depth 5 | Set-Content -Path $outFile -PassThru

Set-AzStorageBlobContent @StorageContainerParams -File $outFile -Blob "$ComponentName/$MetaDataFileName" -Verbose -Force | Format-Table -AutoSize

if ($?)
{
    Write-Verbose -Message 'MetaData File Upload to Blob is Complete' -Verbose
}