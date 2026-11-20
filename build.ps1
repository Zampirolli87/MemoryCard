param([string]$InnoCompiler = "")
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "src"
Push-Location $src
try {
    $archive = Join-Path $src "ludusavi.zip"
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri "https://github.com/mtkennerly/ludusavi/releases/download/v0.31.0/ludusavi-v0.31.0-win64.zip" -OutFile $archive
    }
    $expected = "F47A8AD8C708F01D2EB124704973BEFFAB205E292F5287A10FC4A101F8D68706"
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
        throw "O pacote do Ludusavi não passou na verificação SHA-256."
    }
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (!(Test-Path -LiteralPath $compiler)) { throw "Compilador do .NET Framework não encontrado." }
    $common = @("/nologo", "/target:winexe", "/optimize+", "/warnaserror+", "/platform:x64", "/codepage:65001",
        "/win32icon:MemoryCard.ico", "/resource:ludusavi.zip,LudusaviZip",
        "/resource:Ludusavi-LICENSE.txt,LudusaviLicense", "/reference:System.Windows.Forms.dll",
        "/reference:System.Drawing.dll", "/reference:System.Web.Extensions.dll",
        "/reference:System.IO.Compression.dll", "/reference:System.IO.Compression.FileSystem.dll")
    $sources = @("BackupHydraManual.cs", "MemoryCardDetection.cs", "MemoryCardHistory.cs", "MemoryCardImport.cs", "MemoryCardTheme.cs")
    & $compiler $common "/out:MemoryCard-candidate.exe" $sources
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar a versão instalável." }
    & $compiler $common "/define:PORTABLE" "/out:MemoryCard-Portable.exe" $sources
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar a versão portátil." }
    if ($InnoCompiler) {
        & $InnoCompiler "MemoryCard.iss"
        if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o instalador." }
    }
    Write-Host "Compilação concluída em $src."
} finally { Pop-Location }

