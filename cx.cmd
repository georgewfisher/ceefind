@ECHO OFF

set "T="
set "i="
FOR /F "delims=" %%i IN ('ceefind -first %*') DO set T=%%i
IF "%T%" == [] GOTO WARNING
rem echo out the file name
echo %T%
"%T%"

GOTO END
:WARNING
echo Could not find %*
:END