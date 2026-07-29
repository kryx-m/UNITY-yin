@echo off
cd /d "D:\unityhub\1\My project (第三版)"
git add -A
git commit -m "update: %date% %time%"
git push origin main
echo 推送完成
pause
