$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try
{
    & bash web-service.sh stop
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Stopping the web service failed.'
    }

    & git pull
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Pulling application updates failed. The web service remains stopped.'
    }

    & bash build-web.sh
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Building the web server failed. The web service remains stopped.'
    }

    & bash web-service.sh start
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Starting the web service failed.'
    }
}
finally
{
    Pop-Location
}
