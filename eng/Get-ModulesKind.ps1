[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssemblyPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Returns the TypeWhisperModules assembly metadata of a TypeWhisper.Modules.dll: 'premium' for the optional modules,
# 'stub' for the public stub, or nothing. Reads the metadata tables without loading the assembly.
function Read-SerializedString([byte[]]$Blob, [ref]$Offset) {
    $first = $Blob[$Offset.Value]
    if ($first -eq 0xFF) { $Offset.Value++; return $null }
    if (($first -band 0x80) -eq 0) { $length = $first; $Offset.Value++ }
    elseif (($first -band 0xC0) -eq 0x80) { $length = (($first -band 0x3F) -shl 8) -bor $Blob[$Offset.Value + 1]; $Offset.Value += 2 }
    else {
        $length = (($first -band 0x1F) -shl 24) -bor ($Blob[$Offset.Value + 1] -shl 16) -bor
            ($Blob[$Offset.Value + 2] -shl 8) -bor $Blob[$Offset.Value + 3]
        $Offset.Value += 4
    }
    $value = [Text.Encoding]::UTF8.GetString($Blob, $Offset.Value, $length)
    $Offset.Value += $length
    return $value
}
$stream = [IO.File]::OpenRead((Resolve-Path -LiteralPath $AssemblyPath).Path)
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    try { $hasMetadata = $pe.HasMetadata } catch [BadImageFormatException] { $hasMetadata = $false }
    if (-not $hasMetadata) { throw "$AssemblyPath is not a .NET assembly." }
    $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    $values = @(foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
        $attribute = $metadata.GetCustomAttribute($handle)
        # The framework attribute is always referenced from another assembly; a local type of the same name does not count.
        if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
        $parent = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor).Parent
        if ($parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
        $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$parent)
        if ($metadata.GetString($type.Namespace) -ne 'System.Reflection' -or
            $metadata.GetString($type.Name) -ne 'AssemblyMetadataAttribute') { continue }
        # Blob: prolog 0x0001, then the key and value as serialized strings.
        $blob = $metadata.GetBlobBytes($attribute.Value)
        if ($blob.Length -lt 4 -or $blob[0] -ne 1 -or $blob[1] -ne 0) { throw "$AssemblyPath has malformed assembly metadata." }
        $offset = 2
        $key = Read-SerializedString $blob ([ref]$offset)
        $value = Read-SerializedString $blob ([ref]$offset)
        if ($key -ceq 'TypeWhisperModules') { $value }
    })
} finally { $pe.Dispose(); $stream.Dispose() }
if ($values.Count -gt 1) { throw "$AssemblyPath declares TypeWhisperModules more than once." }
if ($values.Count -eq 1) { return $values[0] }
