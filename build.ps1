$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows 10/11 和 .NET Framework 4.8。' }
$frameworkPath = Split-Path -Parent $compilerPath
$wpfPath = Join-Path $frameworkPath 'WPF'
$sourcePath = Join-Path $PSScriptRoot 'Source'
$outputPath = Join-Path $PSScriptRoot 'FloatTranslate.exe'
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + $outputPath), ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')), ('/win32icon:' + (Join-Path $PSScriptRoot 'app.ico')))
$references = @('System.dll','System.Core.dll','System.Security.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll')
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $frameworkPath $reference) }
foreach ($reference in @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll')) { $arguments += '/reference:' + (Join-Path $wpfPath $reference) }
$arguments += '/resource:' + (Join-Path $sourcePath 'Theme.xaml') + ',Theme.xaml'
$arguments += '/resource:' + (Join-Path $sourcePath 'MainWindow.xaml') + ',MainWindow.xaml'
$arguments += '/resource:' + (Join-Path $PSScriptRoot 'app.ico') + ',app.ico'
$arguments += Get-ChildItem -LiteralPath $sourcePath -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output ('已生成：' + $outputPath)
