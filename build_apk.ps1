# --- CONFIGURAZIONE PERCORSI ---
$rootPath = "C:\Sviluppo\Biliardo_APP_Firebase_CLEAN"
$projectSubDir = "Biliardo.App"
$projectFile = "$rootPath\$projectSubDir\Biliardo.App.csproj"
$apkDest = "$rootPath\APK"
$winDest = "$rootPath\WIN"

# --- PULIZIA E PREPARAZIONE ---
Write-Host "--- Inizio Processo di Build All-in-One ---" -ForegroundColor Cyan

if (!(Test-Path $apkDest)) { New-Item -ItemType Directory -Path $apkDest | Out-Null }
if (!(Test-Path $winDest)) { New-Item -ItemType Directory -Path $winDest | Out-Null }

cd "$rootPath\$projectSubDir"
Write-Host "Pulizia vecchie build..." -ForegroundColor Gray
dotnet clean -c Release | Out-Null

# --- 1. GENERAZIONE ANDROID (APK) ---
Write-Host "Compilazione Android (APK)..." -ForegroundColor Yellow
dotnet publish $projectFile -f net8.0-android -c Release -p:AndroidPackageFormat=apk -p:AndroidKeyStore=false --no-self-contained

$generatedApk = Get-ChildItem "bin\Release\net8.0-android\publish\*-Signed.apk" | Select-Object -First 1
if ($generatedApk) {
    Copy-Item $generatedApk.FullName -Destination "$apkDest\BiliardoApp.apk" -Force
    Write-Host "[OK] Android: APK generato correttamente." -ForegroundColor Green
} else {
    Write-Host "[ERRORE] Android: APK non trovato." -ForegroundColor Red
}

# --- 2. GENERAZIONE WINDOWS (EXE) ---
Write-Host "Compilazione Windows (EXE Unpackaged)..." -ForegroundColor Yellow
dotnet publish $projectFile -f net8.0-windows10.0.19041.0 -c Release -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=None -p:SelfContained=true

$winPublishDir = "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
if (Test-Path $winPublishDir) {
    # Pulisce la cartella WIN prima di copiare (per evitare vecchie DLL)
    Remove-Item -Path "$winDest\*" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -Path "$winPublishDir\*" -Destination $winDest -Recurse -Force
    
    # Rinomina l'eseguibile
    if (Test-Path "$winDest\Biliardo.App.exe") {
        Rename-Item -Path "$winDest\Biliardo.App.exe" -NewName "BiliardoApp.exe" -Force
    }
    Write-Host "[OK] Windows: Eseguibile e dipendenze pronti." -ForegroundColor Green
} else {
    Write-Host "[ERRORE] Windows: Build fallita." -ForegroundColor Red
}

Write-Host "`n--- RESOCONTO FINALE ---" -ForegroundColor Cyan
Write-Host "APK: $apkDest\BiliardoApp.apk"
Write-Host "WIN: $winDest\ (Lancia BiliardoApp.exe)"
Write-Host "------------------------"
Read-Host "Premi Invio per chiudere"