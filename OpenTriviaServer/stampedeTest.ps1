$url = "http://localhost:8080/search?amount=10&type=multiple"

1..100 | ForEach-Object {
    Start-Job -ScriptBlock {
        param($u)
        Invoke-WebRequest -Uri $u -UseBasicParsing | Out-Null
    } -ArgumentList $url
}

Get-Job | Wait-Job
Get-Job | Remove-Job

Write-Host "Gotovo"