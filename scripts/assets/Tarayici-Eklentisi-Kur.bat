@echo off
chcp 65001 >nul
title VortexDownloader - Tarayici Eklentisi Kurulum Yardimcisi
cls
echo ==============================================================================
echo                ⚡ VORTEXDOWNLOADER - TARAYICI ENTEGRASYONU ⚡
echo ==============================================================================
echo.
echo Bu sihirbaz, VortexDownloader indirme yakalama eklentisini
echo Google Chrome, Microsoft Edge veya Brave tarayiciniza eklemenize yardimci olur.
echo.
echo Adimlar:
echo   1. Acilan tarayici sayfasinda sag ustteki "Gelistirici Modu"nu (Developer Mode) acin.
echo   2. "Paketlenmemis oge yukle" (Load unpacked) butonuna tiklayin.
echo   3. Birazdan onunuze acilacak olan "extension" klasorunu secin.
echo.
echo ==============================================================================
echo.
echo Lutfen tarayicinizi secin:
echo   [1] Google Chrome
echo   [2] Microsoft Edge
echo   [3] Brave Browser
echo   [4] Yalnizca klasoru ac
echo.
set /p choice="Seciminiz (1-4): "

if "%choice%"=="1" (
    start chrome chrome://extensions
) else if "%choice%"=="2" (
    start msedge edge://extensions
) else if "%choice%"=="3" (
    start brave brave://extensions
)

echo.
echo 📁 Eklenti klasoru aciliyor...
explorer "%~dp0extension"
echo.
echo [OK] Eklentiyi yukledikten sonra VortexDownloader.exe uygulamasini
echo      calistirarak yuksek hizli indirmelere baslayabilirsiniz.
echo.
pause
