$ErrorActionPreference = 'Stop'
# Parser self-check: pwsh -File selftest.ps1 (after dotnet build -c Release -o out)
Add-Type -Path "$PSScriptRoot\out\Flow.Launcher.Plugin.Choco.dll"
$P = [Flow.Launcher.Plugin.Choco.Parse]
function Check($cond, $msg) { if (-not $cond) { throw "FAIL: $msg" } }

$noise = @"
This is try 1/3. Retrying after 300 milliseconds.
 Access to the path 'C:\ProgramData\chocolatey\choco.exe.old' is denied.
Firefox|151.0.2|160.0.1|false
starship.install|1.25.1|1.27.0|false
bad line|x
"@
$o = $P::OutdatedOutput($noise)
Check ($o.Count -eq 2) "outdated count $($o.Count)"
Check ($o[0].Id -eq 'Firefox' -and $o[0].Available -eq '160.0.1') "outdated fields"

Check ($P::SafeId('starship.install')) "safe id"
Check (-not $P::SafeId('x & calc')) "unsafe id rejected"
Check ($P::MetaBase('starship.install') -eq 'starship') "meta .install"
Check ($P::MetaBase('foo.portable') -eq 'foo') "meta .portable"
Check ($null -eq $P::MetaBase('nushell')) "meta none"
Check ($P::SniffExt([byte[]](0x89,0x50,0x4E,0x47,1)) -eq '.png') "png"
Check ($P::SniffExt([Text.Encoding]::UTF8.GetBytes('<?xml version="1.0"?><svg>')) -eq '.svg') "svg"
Check ($null -eq $P::SniffExt([Text.Encoding]::UTF8.GetBytes('<html>404</html>'))) "html rejected"
Check ($P::Count(111738902) -eq '111.7M') "count"
$SP = [Flow.Launcher.Plugin.Choco.StageParser]
$s = $SP::Of('Progress: Downloading 7zip.install 26.6.0... 45%')
Check ($s.Key -eq 'stage_pkg_download' -and $s.Percent -eq 45 -and $s.Args[0] -eq '7zip.install 26.6.0') "stage pkg download"
$s = $SP::Of('Progress: 72% - Saving 1.08 MB of 1.5 MB')
Check ($s.Key -eq 'stage_file_download' -and $s.Percent -eq 72 -and $s.Args[0] -eq '· 1.08 MB of 1.5 MB') "stage installer download: $($s.Args)"
# real choco line shape
$s = $SP::Of('Progress: 100% - Completed download of C:\Users\x\AppData\Local\Temp\chocolatey\someapp\1.2.3\SomeAppSetup.exe (123.82 MB).')
Check ($s.Percent -eq 100 -and $s.Args[0] -eq '· 123.82 MB') "stage completed download: $($s.Args)"
Check ($SP::Of('Download of SomeAppSetup.exe (123.82 MB) completed.').Key -eq 'stage_verifying') "stage verifying"
Check ($SP::Of('Installing 7zip...').Percent -eq -1) "stage installing"
$s = $SP::Of('You have 7zip v26.5.0 installed. Version 26.6.0 is available based on your source(s).')
Check ($s.Key -eq 'stage_upgrading_from' -and $s.Args[2] -eq '26.6.0') "stage upgrade"
Check ($SP::Of(' chocolatey-misc-helpers.extension has been successfully uninstalled.').Key -eq 'stage_done_uninstalled') "stage done uninstalled"
Check ($null -eq $SP::Of('By installing, you accept licenses for the packages.')) "noise ignored"
$sum = $SP::Summary('Chocolatey installed 0/1 packages. 1 packages failed.')
Check ($sum.Item2 -eq 0 -and $sum.Item3 -eq 1) "summary"

# every key the code uses exists in every language file
$keys = foreach ($f in Get-ChildItem "$PSScriptRoot\Languages\*.xaml") { ,@(([xml](Get-Content $f -Raw)).ResourceDictionary.String.Key) }
$en = $keys[0]; foreach ($k in $keys) { Check ($k.Count -eq $en.Count) "language key count" }
# --- v1.2: sources, packages.config, versions, details, settings filter
$C = [Flow.Launcher.Plugin.Choco.Cli]
$src = $C::ParseSources("chocolatey|https://community.chocolatey.org/api/v2/|False|||0|False|False|False`nold|C:\feed|True|||0|False|False|False`nlocal|C:\feed2|False|||0|False|False|False")
Check ($src.Count -eq 2 -and $src[0].IsCommunity -and -not $src[1].IsCommunity -and $src[1].Name -eq 'local') "sources parse"
$iv = $C::ParseIdVersion("Chocolatey v2.7.5`n7zip|26.6.0`r`nnoise line`nfoo.bar|1.0.0-beta")
Check ($iv.Count -eq 2 -and $iv[1].Item1 -eq 'foo.bar') "id|version parse"
$tmp = [IO.Path]::GetTempFileName()
Set-Content $tmp '<?xml version="1.0"?><packages><package id="7zip" /><package id="git" version="2.0.0" /></packages>'
$pc = $C::ParsePackagesConfig($tmp); Remove-Item $tmp
Check ($pc.Count -eq 2 -and $pc[1].Item2 -eq '2.0.0' -and $null -eq $pc[0].Item2) "packages.config parse"
Check ([Flow.Launcher.Plugin.Choco.DetailsParser]::Deps('a:1.0:|b::') -eq 'a 1.0, b') "deps format"
Check ($P::SafeVersion('1.2.3-beta+5') -and -not $P::SafeVersion('1.0 & calc')) "safe version"
$s = [Flow.Launcher.Plugin.Choco.Settings]::new(); $s.Packages.Add('git')
Check ((-not $s.Selected('git')) -and $s.Selected('7zip')) "exclude mode"
$s.IncludeMode = $true
Check ($s.Selected('GIT') -and -not $s.Selected('7zip')) "include mode"
$w = [Flow.Launcher.Plugin.Choco.Cli]::Wrapper('C:\x\choco.exe', "install a --params `"/x:'y'`" -y", 'C:\t\l.log', 'C:\t\s.stop')
Check ($w.Contains("'/c `"`"C:\x\choco.exe`" install a --params `"/x:''y''`" -y > `"C:\t\l.log`" 2>&1`"'") -and $w.Contains("Test-Path -LiteralPath 'C:\t\s.stop'")) "wrapper quoting"
"selftest OK"