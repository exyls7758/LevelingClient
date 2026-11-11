param([string]$Path = "$PSScriptRoot/../native/Multiverse.Core.dll")
$ErrorActionPreference='Stop'
# Inspect PE imports without requiring Visual Studio or loading vendor drivers.
$bytes=[IO.File]::ReadAllBytes((Resolve-Path $Path))
function U16([int]$offset){[BitConverter]::ToUInt16($bytes,$offset)}
function U32([int]$offset){[BitConverter]::ToUInt32($bytes,$offset)}
$pe=U32 0x3c
if((U32 $pe) -ne 0x4550 -or (U16 ($pe+24)) -ne 0x20b){throw 'Expected PE32+ native DLL'}
$sectionCount=U16 ($pe+6)
$sectionStart=$pe+24+(U16 ($pe+20))
function Offset([uint32]$rva) {
 for($i=0;$i -lt $sectionCount;$i++) {
  $s=$sectionStart+40*$i;$va=U32 ($s+12);$size=[Math]::Max((U32 ($s+8)),(U32 ($s+16)))
  if($rva -ge $va -and $rva -lt $va+$size){return [int]((U32 ($s+20))+$rva-$va)}
 }
 throw 'Invalid PE import RVA'
}
$importRva=U32 ($pe+24+112+8)
if(!$importRva){throw 'Missing import directory'}
$imports=@();$descriptor=Offset $importRva
for($i=0;$i -lt 512;$i++) {
 $nameRva=U32 ($descriptor+20*$i+12);if(!$nameRva){break}
 $start=Offset $nameRva;$end=$start
 while($end -lt $bytes.Length -and $bytes[$end] -ne 0){$end++}
 $imports += [Text.Encoding]::ASCII.GetString($bytes,$start,$end-$start)
}
$forbidden=@($imports | Where-Object {$_ -match '^(msvcp\d|vcruntime\d|concrt\d|nvcuda\.dll$|nvcuvid\.dll$)'})
if($forbidden.Count){throw "Native package requires unbundled/vendor runtime imports: $($forbidden -join ', ')"}
'PASS native imports require no external VC++ runtime or mandatory NVIDIA driver DLL'
