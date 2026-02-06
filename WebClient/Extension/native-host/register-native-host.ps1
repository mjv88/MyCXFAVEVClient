param(
  [Parameter(Mandatory = $true)]
  [string]$ManifestPath
)

$chromeKey = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.mjv88.datevbridge'
$edgeKey = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.mjv88.datevbridge'

New-Item -Path $chromeKey -Force | Out-Null
Set-ItemProperty -Path $chromeKey -Name '(Default)' -Value $ManifestPath

New-Item -Path $edgeKey -Force | Out-Null
Set-ItemProperty -Path $edgeKey -Name '(Default)' -Value $ManifestPath

Write-Host "Registered native host manifest: $ManifestPath"
