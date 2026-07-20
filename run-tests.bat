@echo off
REM Запуск регрессионных тестов форматтера.
REM   run-tests.bat            — все тесты
REM   run-tests.bat --rule 2.6 — только правило 2.6
REM   run-tests.bat -v         — с выводом результата каждого теста
cd /d "%~dp0"
dotnet run -c Release --project TsqlFormatter.Tests -- %*
