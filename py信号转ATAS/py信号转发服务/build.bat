@echo off
chcp 65001 >nul
echo 正在打包 app.py...

cd /d "%~dp0"
pyinstaller -F app.py --name signal_forwarder --clean

echo.
echo 打包完成！可执行文件在 dist 目录中
pause
