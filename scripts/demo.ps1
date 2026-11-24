<#
.SYNOPSIS
  Demonstração ponta a ponta com o ambiente Docker Compose em execução.
  Uso:  pwsh ./scripts/demo.ps1      (ou: powershell -ExecutionPolicy Bypass -File .\scripts\demo.ps1)
#>
$ErrorActionPreference = 'Stop'
$cf = 'http://localhost:8080'
$dc = 'http://localhost:8081'

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Call($method, $url, $token, $body = $null, $headers = @{}) {
    $h = @{ Authorization = "Bearer $token" } + $headers
    $params = @{ Method = $method; Uri = $url; Headers = $h; ContentType = 'application/json'; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status' }
    if ($body) { $params.Body = ($body | ConvertTo-Json -Depth 5) }
    $resp = Invoke-RestMethod @params
    return [pscustomobject]@{ Status = $status; Body = $resp }
}

if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Requer PowerShell 7+ (pwsh). Instale com: winget install Microsoft.PowerShell' }

Step 'Tokens de desenvolvimento (comerciantes A e B)'
$tokenA = (Invoke-RestMethod -Method Post -Uri "$cf/dev/token" -ContentType 'application/json' -Body '{"merchant":"A"}').access_token
$tokenB = (Invoke-RestMethod -Method Post -Uri "$cf/dev/token" -ContentType 'application/json' -Body '{"merchant":"B"}').access_token
Write-Host 'ok'

Step 'Criar conta de caixa do comerciante A com saldo de abertura 1000,00'
$acc = Call Post "$cf/api/v1/cash-accounts" $tokenA @{ name = 'Caixa principal'; currency = 'BRL'; timeZone = 'America/Sao_Paulo'; openingBalance = 1000.00 }
$acc.Status; $acc.Body | Format-List
$accountId = $acc.Body.id

$now = (Get-Date).ToUniversalTime().AddMinutes(-1).ToString('o')
$k1 = [guid]::NewGuid().ToString()

Step 'Crédito de 500,00 (Idempotency-Key nova) → 201'
$c = Call Post "$cf/api/v1/transactions" $tokenA @{ cashAccountId = $accountId; type = 'CREDIT'; amount = 500.00; description = 'Venda'; occurredAt = $now } @{ 'Idempotency-Key' = $k1 }
"$($c.Status) id=$($c.Body.id) businessDate=$($c.Body.businessDate)"

Step 'Repetição com a MESMA chave e mesmo payload → 200 (sem duplicar)'
$r = Call Post "$cf/api/v1/transactions" $tokenA @{ cashAccountId = $accountId; type = 'CREDIT'; amount = 500.00; description = 'Venda'; occurredAt = $now } @{ 'Idempotency-Key' = $k1 }
"$($r.Status) id=$($r.Body.id)"

Step 'Mesma chave com payload diferente → 409'
(Call Post "$cf/api/v1/transactions" $tokenA @{ cashAccountId = $accountId; type = 'CREDIT'; amount = 999.00; occurredAt = $now } @{ 'Idempotency-Key' = $k1 }).Status

Step 'Débito de 200,00 → 201'
$d = Call Post "$cf/api/v1/transactions" $tokenA @{ cashAccountId = $accountId; type = 'DEBIT'; amount = 200.00; description = 'Fornecedor'; occurredAt = $now } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
"$($d.Status) id=$($d.Body.id)"

Step 'Estorno do débito → 201 (lançamento compensatório CREDIT)'
$rev = Call Post "$cf/api/v1/transactions/$($d.Body.id)/reversals" $tokenA @{ reason = 'Lançado incorretamente' } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
"$($rev.Status) type=$($rev.Body.type) reverses=$($rev.Body.reversesTransactionId)"

Step 'Estorno repetido → 409'
(Call Post "$cf/api/v1/transactions/$($d.Body.id)/reversals" $tokenA @{ reason = 'de novo' } @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }).Status

Step 'Isolamento: comerciante B tentando ler/lançar/estornar/consultar dados de A → 404'
"GET transação:    $((Call Get "$cf/api/v1/transactions/$($c.Body.id)" $tokenB).Status)"
"POST lançamento:  $((Call Post "$cf/api/v1/transactions" $tokenB @{ cashAccountId = $accountId; type = 'CREDIT'; amount = 1; occurredAt = $now } @{ 'Idempotency-Key' = 'x' }).Status)"
"POST estorno:     $((Call Post "$cf/api/v1/transactions/$($c.Body.id)/reversals" $tokenB @{ reason = 'x' } @{ 'Idempotency-Key' = 'y' }).Status)"

Step 'Aguardando consolidação assíncrona...'
$today = $c.Body.businessDate
$expected = 1000 + 500 - 200 + 200
for ($i = 0; $i -lt 30; $i++) {
    $bal = Call Get "$dc/api/v1/daily-balances/$($today)?cashAccountId=$accountId" $tokenA
    if ($bal.Status -eq 200 -and [decimal]$bal.Body.closingBalance -eq $expected) { break }
    Start-Sleep -Milliseconds 500
}
$bal.Body | ConvertTo-Json -Depth 5
"Saldo esperado: $expected  →  obtido: $($bal.Body.closingBalance)"

Step 'Consulta por período e estado da projeção'
$from = ([datetime]$today).AddDays(-3).ToString('yyyy-MM-dd')
(Call Get "$dc/api/v1/daily-balances?cashAccountId=$accountId&from=$from&to=$today" $tokenA).Body | ConvertTo-Json -Depth 5
(Call Get "$dc/api/v1/daily-balances/status?cashAccountId=$accountId" $tokenA).Body | ConvertTo-Json -Depth 5

Step 'Isolamento na consolidação: B consultando conta de A → 404'
(Call Get "$dc/api/v1/daily-balances/$($today)?cashAccountId=$accountId" $tokenB).Status

Write-Host "`nConta usada: $accountId" -ForegroundColor Green
