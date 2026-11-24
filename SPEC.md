# SPEC.md --- Plataforma Multiempresa de Fluxo de Caixa

**Versão:** 1.0\
**Status:** Especificação inicial para implementação\
**Stack:** .NET 10, ASP.NET Core, PostgreSQL, RabbitMQ, MassTransit,
Redis, Docker Compose, OpenTelemetry\
**Interface:** APIs REST documentadas com OpenAPI/Swagger\
**Objetivo:** Entregar uma solução demonstrável, testável e preparada
para evolução em cloud.

------------------------------------------------------------------------

## 1. Objetivo e contexto

Construir uma plataforma multiempresa para comerciantes registrarem
créditos e débitos, consultarem lançamentos e consultarem saldos diários
consolidados.

A solução deve conter dois serviços implantáveis independentemente:

1.  **Cash Flow Service:** fonte oficial dos lançamentos financeiros.
    Deve continuar aceitando lançamentos mesmo quando o serviço de
    consolidação estiver indisponível.
2.  **Daily Consolidation Service:** consome eventos publicados pelo
    Cash Flow Service e mantém projeções de leitura para consultas de
    saldo diário.

A comunicação entre os serviços será assíncrona, por eventos via
RabbitMQ. A consistência do consolidado será eventual, com meta inicial
de atualização em até cinco segundos em condições normais de carga.

Esta especificação define uma arquitetura de referência para o desafio.
Metas não explicitamente determinadas pelo enunciado são hipóteses de
projeto e devem ser validadas por testes e, quando necessário,
confirmadas com o avaliador.

## 2. Decisões e premissas aprovadas

  -----------------------------------------------------------------------
  Tema                                Decisão
  ----------------------------------- -----------------------------------
  Framework                           .NET 10

  Hospedagem local                    Docker Compose

  Preparação para cloud               Containers stateless, configuração
                                      externa, health checks e imagens
                                      independentes

  Interface                           APIs REST + Swagger/OpenAPI

  Arquitetura                         Dois microsserviços, DDD tático,
                                      Clean Architecture por serviço

  Integração                          Event-driven com RabbitMQ e
                                      MassTransit

  Consistência                        Eventual no consolidado

  Persistência                        PostgreSQL, banco lógico separado
                                      por serviço

  Cache                               Redis somente como otimização

  Multi-tenancy                       Banco compartilhado por serviço com
                                      isolamento lógico por MerchantId

  Observabilidade                     OpenTelemetry, logs estruturados,
                                      métricas e traces

  Mensageria confiável                Transactional Outbox, consumer
                                      idempotency, retry e error queue

  Dinheiro                            decimal / NUMERIC, nunca float ou
                                      double

  Correções financeiras               Estorno compensatório; lançamentos
                                      confirmados não são editados nem
                                      apagados
  -----------------------------------------------------------------------

### 2.1 Limites de escopo

Não implementar nesta primeira versão: - Interface web ou mobile. -
Kubernetes ou infraestrutura cloud real. - Event Sourcing completo. -
Saga distribuída. - Ledger contábil de partidas dobradas. - Integração
com adquirentes, bancos ou sistemas externos. - Multi-moeda com
conversão cambial.

A estrutura deve permitir evolução futura sem introduzir essas
complexidades antecipadamente.

## 3. Objetivos de negócio e capacidades

### 3.1 Contextos delimitados

**Cash Management / Cash Flow** - Gerenciar contas de caixa de um
comerciante. - Registrar créditos e débitos. - Consultar
movimentações. - Estornar movimentações. - Garantir idempotência e
auditoria. - Publicar eventos de integração.

**Daily Financial Consolidation** - Consumir eventos financeiros. -
Consolidar totais diários por comerciante, conta e moeda. - Calcular
saldo inicial e final. - Expor consultas por data e período. -
Identificar atraso de processamento. - Permitir reconciliação e
reprocessamento operacional.

### 3.2 Atores

-   Comerciante: proprietário dos dados financeiros.
-   Administrador do comerciante: administra usuários e permissões no
    contexto da empresa.
-   Operador financeiro: registra e consulta lançamentos.
-   Consultor financeiro: consulta consolidados.
-   Administrador da plataforma: operação técnica; não recebe acesso
    irrestrito aos dados financeiros por padrão.

Autenticação pode ser simulada localmente por um mecanismo de
desenvolvimento claramente identificado, mas a aplicação deve usar
abstrações compatíveis com JWT/OIDC. Não confiar em `MerchantId` enviado
pelo cliente.

## 4. Requisitos funcionais

### 4.1 Cash Flow Service

  -----------------------------------------------------------------------
  ID                                  Requisito
  ----------------------------------- -----------------------------------
  RF-CF-01                            Criar conta de caixa vinculada a um
                                      comerciante.

  RF-CF-02                            Registrar lançamento de crédito.

  RF-CF-03                            Registrar lançamento de débito.

  RF-CF-04                            Consultar lançamento por
                                      identificador.

  RF-CF-05                            Consultar lançamentos por conta e
                                      período, com paginação.

  RF-CF-06                            Impedir duplicação da mesma
                                      operação pela mesma chave de
                                      idempotência.

  RF-CF-07                            Estornar lançamento confirmado por
                                      operação compensatória.

  RF-CF-08                            Registrar data/hora da ocorrência e
                                      data/hora de criação.

  RF-CF-09                            Persistir evento na Outbox na mesma
                                      transação do lançamento.

  RF-CF-10                            Expor health checks de liveness e
                                      readiness.
  -----------------------------------------------------------------------

### 4.2 Daily Consolidation Service

  -----------------------------------------------------------------------
  ID                                  Requisito
  ----------------------------------- -----------------------------------
  RF-DC-01                            Consumir eventos de lançamento
                                      criados e estornados.

  RF-DC-02                            Processar cada EventId de forma
                                      idempotente.

  RF-DC-03                            Consolidar por MerchantId,
                                      CashAccountId, BusinessDate e
                                      Currency.

  RF-DC-04                            Calcular créditos, débitos, saldo
                                      inicial e saldo final.

  RF-DC-05                            Consultar consolidado de um dia.

  RF-DC-06                            Consultar consolidado por período.

  RF-DC-07                            Informar timestamp de atualização e
                                      estado de defasagem.

  RF-DC-08                            Recuperar processamento após
                                      indisponibilidade do consumidor.

  RF-DC-09                            Expor health checks.

  RF-DC-10                            Permitir reconstrução/reconciliação
                                      da projeção a partir dos
                                      dados/eventos disponíveis, por
                                      procedimento operacional
                                      documentado.
  -----------------------------------------------------------------------

## 5. Regras de negócio

1.  Todo dado financeiro pertence a exatamente um comerciante e uma
    conta de caixa.
2.  Uma conta pertence a um comerciante; não é permitido referenciar
    conta de outro comerciante.
3.  Valores devem ser positivos e maiores que zero. A natureza do
    lançamento é definida por `type` (`CREDIT` ou `DEBIT`).
4.  Usar `decimal` no .NET e `NUMERIC(19,4)` no PostgreSQL. Não usar
    `float` ou `double`.
5.  Moeda inicial: BRL. Persistir o código ISO 4217 mesmo que a primeira
    versão aceite apenas BRL.
6.  Lançamento confirmado é imutável. Correções são feitas por estorno,
    criando lançamento compensatório ligado ao original.
7.  Estorno não pode ser repetido para o mesmo lançamento, salvo se
    houver uma regra explícita de estorno parcial em evolução futura. Na
    primeira versão, estorno integral.
8.  Idempotência é definida por comerciante + operação + chave. A mesma
    chave com payload diferente deve retornar conflito.
9.  `OccurredAt` representa quando o fato financeiro ocorreu.
    `CreatedAt` representa quando o sistema registrou o fato.
10. `BusinessDate` é calculada pelo Cash Flow Service a partir de
    `OccurredAt` convertido para o fuso horário configurado na
    conta/comerciante. O consumidor usa a data explícita do evento; não
    recalcula com seu próprio relógio.
11. Fuso horário deve usar identificador IANA (ex.: `America/Sao_Paulo`)
    e ser validado.
12. Saldo inicial de uma conta deve ser representado por lançamento de
    abertura (`OPENING_BALANCE`) ou operação equivalente auditável. Não
    manter um saldo inicial mutável e desconectado do histórico.
13. Saldo diário:
    `ClosingBalance = OpeningBalance + TotalCredits - TotalDebits`.
14. O saldo inicial de um dia é o saldo final do dia anterior; para o
    primeiro dia, é o saldo de abertura.
15. Lançamentos retroativos são permitidos na primeira versão. Ao
    afetarem uma data passada, a projeção daquele dia e dos dias
    subsequentes da mesma conta/moeda precisa ser recalculada ou
    atualizada de forma consistente.
16. Uma conta não deve misturar moedas. Uma conta tem uma única moeda.
17. Saldo negativo é permitido na primeira versão, mas deve ser
    apresentado sem truncamento ou correção automática.
18. Não existe garantia de ordenação global no RabbitMQ. O consumidor
    deve suportar duplicidade e eventos fora de ordem.
19. Eventos financeiros confirmados não podem ser descartados
    silenciosamente. Mensagens em erro precisam de alerta, retenção e
    procedimento de reprocessamento.
20. O serviço de consolidação não acessa o banco do Cash Flow Service.

## 6. Requisitos não funcionais e metas

  -----------------------------------------------------------------------
  ID                      Requisito               Meta inicial
  ----------------------- ----------------------- -----------------------
  RNF-01                  Independência           Cash Flow aceita e
                                                  persiste lançamentos
                                                  com Consolidation
                                                  indisponível.

  RNF-02                  Carga de consulta       50 requisições por
                                                  segundo na API de
                                                  consolidação.

  RNF-03                  Sucesso                 Pelo menos 95% de
                                                  respostas bem-sucedidas
                                                  no cenário de carga
                                                  definido.

  RNF-04                  Atualização eventual    Meta de até 5 segundos
                                                  entre commit do
                                                  lançamento e projeção
                                                  atualizada em carga
                                                  nominal.

  RNF-05                  Latência                p95 de consulta até 300
                                                  ms no ambiente de teste
                                                  dimensionado.

  RNF-06                  Integridade             Zero perda de
                                                  lançamento confirmado;
                                                  eventos podem ser
                                                  entregues mais de uma
                                                  vez, mas efeitos devem
                                                  ser idempotentes.

  RNF-07                  Escala                  APIs stateless e
                                                  consumidores escaláveis
                                                  horizontalmente,
                                                  respeitando
                                                  concorrência por conta.

  RNF-08                  Segurança               Isolamento entre
                                                  comerciantes validado
                                                  por testes negativos.

  RNF-09                  Observabilidade         Logs estruturados,
                                                  métricas e tracing
                                                  distribuído.

  RNF-10                  Recuperação             Backlog deve ser
                                                  processado após
                                                  recuperação do
                                                  consumidor, sem
                                                  intervenção manual em
                                                  falhas transitórias.
  -----------------------------------------------------------------------

A meta de cinco segundos é um SLO inicial de processamento e não uma
garantia incondicional. Deve ser medida e revisada conforme volume,
infraestrutura e comportamento de falhas.

A exigência de "máximo de 5% de perda de requisições" deve ser
interpretada como taxa máxima de falha de requisições da API de
consolidação no cenário acordado. Isso não autoriza perda de lançamentos
financeiros confirmados. Documentar essa distinção.

### 6.1 Definições para teste de carga

-   Carga sustentada: 50 RPS para endpoints de consulta de consolidação.
-   Duração inicial: 15 minutos, após aquecimento.
-   Cenário adicional: pico de 50 RPS por 5 minutos.
-   Medir: throughput, taxa HTTP 2xx/4xx/5xx, timeout, p50/p95/p99, CPU,
    memória, pool de conexões, latência do PostgreSQL, cache hit ratio.
-   Dados de teste devem representar vários comerciantes e contas,
    evitando que um único tenant distorça os resultados.
-   Documentar hardware/limites Docker, dataset, ferramenta e comando
    utilizados.
-   Não declarar metas atingidas sem relatório de execução.

## 7. Arquitetura lógica

### 7.1 Componentes

**Cash Flow Service** - REST API ASP.NET Core. - Application layer com
casos de uso. - Domain layer com entidades e regras. - Infrastructure
com EF Core/Npgsql, Outbox e MassTransit publisher. - PostgreSQL
próprio.

**Daily Consolidation Service** - REST API ASP.NET Core para leitura. -
Worker/consumer MassTransit para eventos. - Application layer com casos
de uso de consulta e processamento. - Domain layer com regras de
consolidação. - Infrastructure com EF Core/Npgsql, RabbitMQ e Redis. -
PostgreSQL próprio.

**Infraestrutura compartilhada local** - RabbitMQ com painel de
administração. - Redis. - PostgreSQL em instâncias ou databases
separados por serviço. - OpenTelemetry Collector, Prometheus, Grafana e
Jaeger ou Tempo, Loki opcional.

### 7.2 Fluxo de escrita

1.  Cliente chama `POST /api/v1/transactions`.
2.  API autentica usuário e resolve o comerciante autorizado a partir do
    contexto de identidade.
3.  Application valida conta, valor, moeda, permissões e idempotência.
4.  Uma transação PostgreSQL grava lançamento e mensagem Outbox.
5.  Commit é realizado.
6.  API responde `201 Created`.
7.  Background publisher lê Outbox e publica evento no RabbitMQ com
    publisher confirms.
8.  Publisher marca evento como publicado somente após confirmação do
    broker.

A publicação pode ocorrer após a resposta HTTP. A Outbox garante que a
intenção de publicar não se perca se o processo cair depois do commit.

### 7.3 Fluxo de consumo

1.  RabbitMQ entrega evento ao consumer.
2.  Consumer valida versão, campos obrigatórios e identidade do evento.
3.  Em uma transação do banco de consolidação, registra EventId na
    tabela de eventos processados e atualiza a projeção.
4.  Se EventId já foi processado, não aplicar novamente; confirmar a
    mensagem.
5.  ACK somente depois do commit no banco de consolidação.
6.  Falha transitória: retry limitado com backoff.
7.  Falha permanente ou tentativas esgotadas: error queue/DLQ, alerta e
    procedimento de reprocessamento.

Não manter transação de banco aberta durante chamadas de rede.

### 7.4 Fluxo de consulta

1.  Cliente consulta a API de consolidação.
2.  API resolve tenant pela identidade autenticada.
3.  Consulta Redis usando chave com tenant, conta, moeda e período, ou
    consulta PostgreSQL.
4.  Resposta inclui `lastUpdatedAt` e indicador de consistência.
5.  Redis é cache descartável; PostgreSQL da consolidação é a fonte da
    projeção.

## 8. Contratos de API

Todos os endpoints de negócio devem exigir autenticação em ambientes não
locais. Usar `application/json`, OpenAPI e versionamento `/api/v1`.

### 8.1 Contexto de autenticação e multi-tenancy

O `MerchantId` não deve ser aceito como fonte de autorização a partir do
body ou query string. Resolver o tenant por claims confiáveis e
associação usuário-comerciante no Identity Provider ou mecanismo de
autorização.

Claims sugeridas: - `sub`: usuário. - `merchant_id`: comerciante ativo,
se o IdP suportar esse modelo. - `roles` ou `scope`: permissões.

Se um usuário puder atuar em mais de um comerciante, implementar seleção
explícita de contexto com validação server-side de associação. Não
confiar apenas em um header arbitrário.

### 8.2 Cash Flow endpoints

#### Criar conta

`POST /api/v1/cash-accounts`

Request:

``` json
{
  "name": "Caixa principal",
  "currency": "BRL",
  "timeZone": "America/Sao_Paulo"
}
```

Response `201 Created`:

``` json
{
  "id": "UUID",
  "name": "Caixa principal",
  "currency": "BRL",
  "timeZone": "America/Sao_Paulo",
  "createdAt": "2026-11-17T20:00:00Z"
}
```

#### Registrar lançamento

`POST /api/v1/transactions`

Headers:

``` http
Authorization: Bearer <token>
Idempotency-Key: <unique-client-key>
Content-Type: application/json
```

Request:

``` json
{
  "cashAccountId": "UUID",
  "type": "CREDIT",
  "amount": 500.00,
  "description": "Venda de produtos",
  "occurredAt": "2026-11-17T14:30:00-03:00"
}
```

Response `201 Created`:

``` json
{
  "id": "UUID",
  "cashAccountId": "UUID",
  "type": "CREDIT",
  "amount": 500.00,
  "currency": "BRL",
  "businessDate": "2026-11-17",
  "status": "CONFIRMED",
  "createdAt": "2026-11-17T17:30:00Z"
}
```

Com a mesma chave e mesmo payload, retornar o resultado original (ou
`200 OK`, documentando a escolha). Mesma chave com payload diferente:
`409 Conflict`.

#### Consultar lançamentos

`GET /api/v1/transactions?cashAccountId={id}&from={date}&to={date}&page=1&pageSize=50`

-   Filtrar sempre pelo tenant autenticado.
-   Limitar `pageSize` (máximo sugerido: 100).
-   Ordenação estável por `OccurredAt` e `Id`.
-   Retornar metadados de paginação.

#### Consultar lançamento

`GET /api/v1/transactions/{id}`

Retornar `404` quando não existir ou não pertencer ao tenant, evitando
vazamento de existência de dados de outro tenant.

#### Estornar lançamento

`POST /api/v1/transactions/{id}/reversals`

Request:

``` json
{
  "reason": "Lançamento registrado incorretamente",
  "occurredAt": "2026-11-17T15:00:00-03:00"
}
```

Criar lançamento compensatório de natureza inversa, referenciando o
original. Não alterar nem apagar o registro original. Publicar evento
`CashTransactionReversed` ou evento canônico de transação criada com
referência de estorno; escolher um único contrato e documentá-lo.

### 8.3 Daily Consolidation endpoints

#### Consultar consolidado por período

`GET /api/v1/daily-balances?cashAccountId={id}&from=2026-11-01&to=2026-11-30`

Response `200 OK`:

``` json
{
  "cashAccountId": "UUID",
  "currency": "BRL",
  "from": "2026-11-01",
  "to": "2026-11-30",
  "openingBalance": 1000.00,
  "totalCredits": 5000.00,
  "totalDebits": 2500.00,
  "closingBalance": 3500.00,
  "consistency": {
    "status": "CURRENT",
    "lastUpdatedAt": "2026-11-17T17:30:00Z",
    "lagSeconds": 1
  },
  "days": []
}
```

#### Consultar um dia

`GET /api/v1/daily-balances/{businessDate}?cashAccountId={id}`

Retornar créditos, débitos, saldo inicial, saldo final, moeda e
atualização.

#### Consultar estado

`GET /api/v1/daily-balances/status?cashAccountId={id}`

Retornar estado da projeção e último evento aplicado. Não expor detalhes
internos de filas.

Estados: - `CURRENT`: sem atraso acima do limite operacional. -
`PROCESSING`: eventos conhecidos aguardando processamento. - `DELAYED`:
atraso acima do SLO. - `UNKNOWN`: não foi possível determinar o estado.

O status `CURRENT` deve ser calculado com base em watermark/posição de
processamento conhecida, não apenas no horário de atualização de uma
linha.

### 8.4 Erros

Usar `ProblemDetails` (RFC 9457/ASP.NET Core ProblemDetails) com: -
`type` - `title` - `status` - `detail` sem dados sensíveis -
`instance` - `traceId` - `errors` para validação, quando aplicável.

Códigos esperados: - `400`: payload inválido. - `401`: não
autenticado. - `403`: autenticado sem permissão. - `404`: recurso não
encontrado no tenant. - `409`: conflito de idempotência ou estado. -
`422`: regra de negócio não atendida, se a convenção adotada usar esse
código. - `429`: rate limit. - `500`: falha inesperada. - `503`:
dependência essencial indisponível para aquela operação.

## 9. Contratos de eventos

Eventos são contratos públicos entre bounded contexts. Não compartilhar
classes de domínio entre serviços. Criar contratos versionados e
serializados em JSON.

### 9.1 CashTransactionCreated v1

``` json
{
  "eventId": "UUID",
  "eventType": "CashTransactionCreated",
  "schemaVersion": 1,
  "occurredOn": "2026-11-17T17:30:00Z",
  "merchantId": "UUID",
  "cashAccountId": "UUID",
  "transactionId": "UUID",
  "transactionType": "CREDIT",
  "amount": 500.00,
  "currency": "BRL",
  "businessDate": "2026-11-17",
  "reversesTransactionId": null,
  "correlationId": "UUID"
}
```

Requisitos: - `eventId` estável e único. - `merchantId`, `cashAccountId`
e `transactionId` obrigatórios. - `amount` decimal positivo. -
`businessDate` calculada no produtor. - `schemaVersion` explícita. - Não
incluir dados pessoais desnecessários. - Propagar correlation/trace
context quando suportado. - Compatibilidade retroativa: adicionar campos
opcionais; mudanças incompatíveis exigem nova versão.

### 9.2 Estornos

Preferência para a primeira versão: publicar o mesmo evento canônico
`CashTransactionCreated` para a transação compensatória, preenchendo
`reversesTransactionId` e invertendo `transactionType`. Isso reduz a
quantidade de handlers e mantém uma única representação contábil de
movimentação.

### 9.3 Entrega e ordenação

Assumir entrega at-least-once. Não prometer exactly-once end-to-end.
Garantir efeito idempotente no banco do consumidor.

Eventos de uma mesma conta podem chegar fora de ordem. A consolidação
deve: - Agregar movimentos por data, sem depender da ordem para total
diário. - Recalcular saldos afetados quando chegar evento retroativo. -
Usar concorrência controlada/lock por conta ou estratégia equivalente. -
Não depender de ordenação global da fila. - Não ACK antes do commit.

## 10. Modelo de dados

### 10.1 Cash Flow PostgreSQL

Tabelas mínimas:

**Merchants** - `Id UUID PK` - `Name VARCHAR(200)` -
`TimeZone VARCHAR(100)` - `Status VARCHAR(30)` - `CreatedAt TIMESTAMPTZ`

**CashAccounts** - `Id UUID PK` - `MerchantId UUID NOT NULL` -
`Name VARCHAR(150) NOT NULL` - `Currency CHAR(3) NOT NULL` -
`TimeZone VARCHAR(100) NOT NULL` - `CreatedAt TIMESTAMPTZ` -
`Status VARCHAR(30) NOT NULL`

**CashTransactions** - `Id UUID PK` - `MerchantId UUID NOT NULL` -
`CashAccountId UUID NOT NULL` - `Type VARCHAR(30) NOT NULL` -
`Amount NUMERIC(19,4) NOT NULL` - `Currency CHAR(3) NOT NULL` -
`Description VARCHAR(500)` - `OccurredAt TIMESTAMPTZ NOT NULL` -
`BusinessDate DATE NOT NULL` - `CreatedAt TIMESTAMPTZ NOT NULL` -
`IdempotencyKey VARCHAR(200) NOT NULL` -
`ReversesTransactionId UUID NULL`

Constraints: - `Amount > 0`. - Tipo limitado a `CREDIT` ou `DEBIT` (e
`OPENING_BALANCE`, se adotado como tipo explícito). - FK composta que
garanta que a conta pertence ao mesmo `MerchantId`. - Unicidade de
`(MerchantId, IdempotencyKey)` para o escopo de operação definido. - Um
único estorno integral por transação original na primeira versão. -
Índice `(MerchantId, CashAccountId, OccurredAt, Id)`. - Índice
`(MerchantId, CashAccountId, BusinessDate)`.

**OutboxMessages** - `Id UUID PK` - `EventType VARCHAR(200)` -
`SchemaVersion INT` - `Payload JSONB` - `CreatedAt TIMESTAMPTZ` -
`PublishedAt TIMESTAMPTZ NULL` - `RetryCount INT` -
`LastError TEXT NULL`

Índices para buscar mensagens não publicadas. Implementar claim
concorrente seguro (por exemplo, `FOR UPDATE SKIP LOCKED`) e evitar dois
publishers processarem a mesma linha simultaneamente.

**IdempotencyRecords** (separar de CashTransactions caso seja necessário
guardar hash e resposta) - `MerchantId` - `Operation` -
`IdempotencyKey` - `RequestHash` - `ResourceId` - `ResponseSnapshot`
opcional - `CreatedAt` - Unique
`(MerchantId, Operation, IdempotencyKey)`.

Definir retenção da chave de idempotência de forma explícita; para
lançamentos financeiros, não expirar antes da janela de risco definida.
A primeira versão pode manter registro permanente.

### 10.2 Consolidation PostgreSQL

**DailyBalances** - `MerchantId UUID NOT NULL` -
`CashAccountId UUID NOT NULL` - `BusinessDate DATE NOT NULL` -
`Currency CHAR(3) NOT NULL` - `OpeningBalance NUMERIC(19,4) NOT NULL` -
`TotalCredits NUMERIC(19,4) NOT NULL` -
`TotalDebits NUMERIC(19,4) NOT NULL` -
`ClosingBalance NUMERIC(19,4) NOT NULL` -
`UpdatedAt TIMESTAMPTZ NOT NULL` - PK/unique
`(MerchantId, CashAccountId, BusinessDate, Currency)`.

**ProcessedEvents** - `EventId UUID NOT NULL` -
`ConsumerName VARCHAR(150) NOT NULL` -
`ProcessedAt TIMESTAMPTZ NOT NULL` - Unique `(EventId, ConsumerName)`.

**ConsumerCheckpoints / ProjectionWatermarks** (quando necessário) -
Identificar última posição/evento conhecido processado por partição ou
conta. - Não assumir que UUID representa ordem temporal. - Não marcar
como atualizado se existirem lacunas conhecidas.

Opcionalmente manter `DailyMovements`/registro de movimentos aplicados
na base de projeção para permitir reconstrução determinística e correção
de eventos retroativos. Se não for armazenado, documentar como a
reconciliação recuperará a origem dos movimentos sem acessar o banco do
produtor.

### 10.3 Isolamento multiempresa

-   Todas as tabelas de negócio incluem `MerchantId`.
-   Consultas e comandos recebem tenant do contexto autenticado.
-   Não aceitar tenant do body como autoridade.
-   Validar propriedade de `CashAccountId` antes de criar transação.
-   Usar filtros globais do EF Core apenas como defesa auxiliar; não
    como única barreira.
-   Avaliar PostgreSQL Row-Level Security (RLS) como defesa em
    profundidade. Se implementada, definir tenant por conexão/transação
    e garantir reset seguro em pooling.
-   Chaves Redis incluem tenant:
    `daily-balance:{merchantId}:{accountId}:{date}:{currency}`.
-   Logs não devem expor tokens, payloads financeiros completos ou dados
    sensíveis desnecessários.
-   Consumidores validam campos tenant do evento e persistem MerchantId
    em todas as projeções.
-   Testes negativos obrigatórios para leitura, escrita, estorno e
    consulta de conta de outro tenant.

## 11. Estratégia de consolidação e correções

A projeção deve ser determinística.

Para cada conta/moeda: 1. Identificar movimentos por `BusinessDate`. 2.
Somar créditos e débitos. 3. Calcular saldo final de cada dia. 4.
Propagar saldo final como abertura do dia seguinte. 5. Em evento
retroativo, recalcular o dia afetado e todos os dias posteriores daquela
conta, dentro de uma transação ou por processo de reconstrução com
controle de versão.

Para evitar bloqueios excessivos: - Serializar processamento por conta,
ou usar lock transacional/advisory lock por conta. - Permitir
paralelismo entre contas diferentes. - Evitar paralelismo concorrente de
eventos da mesma conta se isso comprometer a projeção. - Medir duração e
backlog do recálculo.

A primeira versão deve privilegiar correção financeira e simplicidade.
Otimizações incrementais podem ser feitas após testes.

## 12. Resiliência

### 12.1 HTTP

Usar `Microsoft.Extensions.Http.Resilience` para chamadas HTTP de saída
que existirem. Não criar retry genérico em toda operação.

-   Timeout por dependência.
-   Retry apenas para falhas transitórias e operações
    seguras/idempotentes.
-   Circuit breaker para proteger dependências remotas.
-   Não fazer retry cego de POST financeiro sem idempotency key.
-   Evitar retry duplicado em múltiplas camadas.

Os serviços centrais se comunicam por RabbitMQ, não por chamada síncrona
de consolidação.

### 12.2 Mensageria

-   Outbox no produtor.
-   Publisher confirms.
-   Mensagens persistentes e filas duráveis.
-   Retry limitado com backoff para falhas transitórias.
-   Error queue/DLQ para falhas definitivas.
-   Idempotência transacional no consumidor.
-   ACK após commit.
-   Alertas para mensagens em erro e backlog crescente.
-   Procedimento de replay documentado e auditado.

### 12.3 Banco de dados

-   Timeouts e pool de conexões configurados.
-   Retry de conexão transitória somente em operações seguras, com
    estratégia EF Core apropriada.
-   Transações curtas.
-   Backup e restore documentados.
-   Migrations versionadas.
-   Não compartilhar transações entre serviços.

## 13. Segurança

### 13.1 Autenticação e autorização

-   ASP.NET Core Authentication/Authorization.
-   JWT Bearer e validação de issuer, audience, assinatura e expiração.
-   OIDC como integração de produção.
-   Policies para permissões como `transactions:write`,
    `transactions:read`, `balances:read`, `accounts:manage`.
-   Autorização sempre combinada com tenant.
-   Não confiar em headers de identidade não autenticados.

Para desenvolvimento local, fornecer uma configuração de autenticação de
desenvolvimento que seja impossível ativar inadvertidamente em produção.
Documentar claramente suas limitações. Preferir um Identity Provider
local em container se isso não comprometer a simplicidade.

### 13.2 Proteção de infraestrutura

-   TLS em ambientes cloud.
-   Credenciais não versionadas.
-   `.env.example` sem segredos reais.
-   RabbitMQ com usuário e senha dedicados, permissões mínimas e vhosts.
-   Redis sem exposição pública.
-   PostgreSQL sem exposição pública em produção.
-   Rate limiting nas APIs.
-   Limites de payload e validação de entrada.
-   Dependabot ou análise de vulnerabilidades de dependências.
-   Logs sem tokens e sem segredos.

## 14. Observabilidade

Instrumentar APIs, EF Core, HttpClient quando aplicável e MassTransit
com OpenTelemetry.

Métricas mínimas: - Request rate, erro e latência por endpoint. - p50,
p95 e p99. - Consumer duration e falhas. - RabbitMQ queue depth e
unacked messages. - Outbox pending count e idade do evento mais
antigo. - Consolidation lag. - Eventos processados por minuto. -
Duplicatas detectadas. - Reprocessamentos e error queue count. -
PostgreSQL connections, query duration e pool wait. - Redis hit/miss e
latência.

Logs estruturados devem incluir `TraceId`, `SpanId`, `CorrelationId`,
`EventId`, `MerchantId` (se permitido pela política de privacidade) e
identificador do serviço. Não registrar dados financeiros completos
desnecessariamente.

Dashboards: 1. Saúde das APIs. 2. Pipeline de eventos. 3. Atraso da
consolidação. 4. Erros e mensagens em quarentena. 5. Performance e
recursos.

Alertas: - Lag superior a cinco segundos por janela definida. - Outbox
mais antigo acima do limite operacional. - Error queue maior que zero ou
crescendo. - Taxa de erro acima do SLO. - Banco sem conexões
disponíveis. - Serviço sem readiness.

## 15. Stack e pacotes

Versões NuGet devem ser compatíveis com .NET 10 e fixadas no
repositório. Claude Code deve verificar versões estáveis e compatíveis
no momento da implementação, sem assumir versões de pacote não
verificadas.

Pacotes/categorias: - `Microsoft.AspNetCore.OpenApi` / integração
Swagger compatível com .NET 10. - `Microsoft.EntityFrameworkCore`. -
`Npgsql.EntityFrameworkCore.PostgreSQL`. - `MassTransit`. -
`MassTransit.RabbitMQ`. - `Microsoft.Extensions.Http.Resilience`. -
`StackExchange.Redis`. - `OpenTelemetry.Extensions.Hosting`. -
Instrumentações OpenTelemetry para ASP.NET Core, HttpClient, Npgsql e
runtime. - FluentValidation (opcional) ou validação nativa. - xUnit,
FluentAssertions (ou asserts nativos), Testcontainers. - Testcontainers
para PostgreSQL, RabbitMQ e Redis quando apropriado. - NBomber ou k6
para carga.

Evitar instalar pacotes redundantes. Usar central package management
(`Directory.Packages.props`) e versões fixas.

## 16. Estrutura de repositório

``` text
cash-flow-platform/
├── src/
│   ├── Services/
│   │   ├── CashFlow/
│   │   │   ├── CashFlow.Api/
│   │   │   ├── CashFlow.Application/
│   │   │   ├── CashFlow.Domain/
│   │   │   └── CashFlow.Infrastructure/
│   │   └── DailyConsolidation/
│   │       ├── DailyConsolidation.Api/
│   │       ├── DailyConsolidation.Application/
│   │       ├── DailyConsolidation.Domain/
│   │       └── DailyConsolidation.Infrastructure/
│   └── BuildingBlocks/
│       ├── Observability/
│       └── Contracts/
├── tests/
│   ├── CashFlow.UnitTests/
│   ├── CashFlow.IntegrationTests/
│   ├── Consolidation.UnitTests/
│   ├── Consolidation.IntegrationTests/
│   ├── ContractTests/
│   ├── ResilienceTests/
│   └── LoadTests/
├── deploy/
│   ├── docker/
│   ├── rabbitmq/
│   └── observability/
├── docs/
│   ├── architecture/
│   ├── adr/
│   ├── business/
│   ├── requirements/
│   ├── integration/
│   ├── security/
│   ├── testing/
│   └── operations/
├── docker-compose.yml
├── .env.example
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── README.md
├── SPEC.md
└── .github/workflows/ci.yml
```

### 16.1 Dependências entre projetos

Em cada serviço: - Domain não referencia outros projetos. - Application
referencia Domain. - Infrastructure referencia Application e Domain. -
API referencia Application e registra Infrastructure via DI. - Testes
referenciam somente os projetos necessários.

Não criar projeto compartilhado com entidades de domínio comuns.
Compartilhar somente contratos de integração versionados e componentes
técnicos mínimos.

## 17. Docker Compose

O ambiente local deve subir: - `cashflow-api` - `cashflow-worker` ou
hosted service de Outbox dentro do Cash Flow -
`daily-consolidation-api` - `daily-consolidation-consumer` (pode ser
worker separado ou hosted service no serviço) - `postgres-cashflow` -
`postgres-consolidation` - `rabbitmq` - `redis` - `otel-collector` -
`prometheus` - `grafana` - `jaeger` ou `tempo` - `loki` opcional

Preferir containers separados para API e worker quando a separação
facilitar escala e falhas independentes. Se compartilhar o mesmo
executável inicialmente, permitir ativar/desativar cada hosted service
por configuração.

Requisitos: - Health checks e `depends_on` com
`condition: service_healthy` quando suportado. - Volumes nomeados para
persistência local. - Redes internas para bancos e broker. - Portas
locais documentadas. - Secrets por variáveis de ambiente, nunca
hardcoded. - `docker compose up --build` deve iniciar o ambiente. -
Migrations executadas por etapa controlada (comando/init job), não por
todas as réplicas simultaneamente em produção. - Imagens multi-stage,
usuário não-root e health check de container quando possível.

## 18. Estratégia de testes

### 18.1 Unitários

Cash Flow: - Crédito válido. - Débito válido. - Valor zero/negativo
rejeitado. - Conta de outro comerciante rejeitada. - Estorno integral. -
Estorno duplicado rejeitado. - Data operacional calculada com timezone
correto. - Idempotência e conflito de payload.

Consolidação: - Soma de créditos e débitos. - Saldo inicial/final. - Dia
sem movimentação. - Evento duplicado. - Evento retroativo. - Evento fora
de ordem. - Isolamento por conta, moeda e comerciante.

### 18.2 Integração

-   Persistência PostgreSQL.
-   Commit atômico de transação + Outbox.
-   Publicação Outbox para RabbitMQ.
-   Consumo e ACK somente após commit.
-   Processamento duplicado sem duplicar efeito.
-   Error queue em falha permanente.
-   Cache Redis e fallback para PostgreSQL.
-   Migrations em banco vazio.

Usar Testcontainers para dependências reais quando possível. Não
substituir todos os testes de integração por mocks.

### 18.3 Testes de resiliência

1.  Parar Daily Consolidation.
2.  Criar lançamentos.
3.  Verificar respostas de sucesso e persistência.
4.  Verificar Outbox e/ou fila acumulada.
5.  Reiniciar consumidor.
6.  Confirmar projeção correta e ausência de duplicidade.

Outros cenários: - RabbitMQ indisponível durante publicação. - Publisher
cai após broker confirmar e antes de marcar Outbox. - Consumer cai após
commit e antes do ACK. - PostgreSQL de consolidação indisponível. -
Redis indisponível. - Mensagem malformada. - Evento duplicado e fora de
ordem. - Reinício de containers com volumes persistentes.

### 18.4 Testes de contrato

Validar: - JSON schema/event version. - Campos obrigatórios. -
Compatibilidade de versões. - Contratos OpenAPI. - Erros ProblemDetails.

### 18.5 Testes de carga

Criar cenário reproduzível com 50 RPS na API de consolidação. Gerar
dataset multiempresa. Medir resultados e guardar relatório no
repositório em `docs/testing/`.

Não misturar carga de escrita e leitura sem identificar o cenário. Criar
cenários distintos e um cenário combinado.

## 19. Critérios de aceite

O projeto só será considerado pronto quando:

### Funcionalidade

-   [ ] Criar conta de caixa.
-   [ ] Registrar crédito.
-   [ ] Registrar débito.
-   [ ] Consultar lançamentos com paginação.
-   [ ] Estornar lançamento.
-   [ ] Consultar consolidado diário e por período.
-   [ ] Apresentar última atualização e estado da projeção.

### Integridade e isolamento

-   [ ] Transação e Outbox são atômicas.
-   [ ] Repetição da mesma idempotency key não duplica lançamento.
-   [ ] Repetição de evento não duplica consolidação.
-   [ ] Usuário de um comerciante não acessa dados de outro.
-   [ ] Conta não pode ser usada por comerciante diferente.
-   [ ] Valores financeiros usam decimal/NUMERIC.
-   [ ] Estornos preservam histórico.

### Resiliência

-   [ ] Lançamentos funcionam com consolidação desligada.
-   [ ] Eventos pendentes são processados após recuperação.
-   [ ] Falha do consumidor não causa ACK prematuro.
-   [ ] Error queue é observável e documentada.
-   [ ] Redis indisponível não torna a consulta impossível se PostgreSQL
    estiver saudável.
-   [ ] Mensagens duplicadas e fora de ordem são tratadas.

### Qualidade e operação

-   [ ] Testes unitários e de integração passam.
-   [ ] Teste de 50 RPS executado e relatório publicado.
-   [ ] OpenAPI disponível.
-   [ ] Health checks implementados.
-   [ ] Logs, métricas e traces disponíveis localmente.
-   [ ] Docker Compose sobe a solução do zero.
-   [ ] README documenta pré-requisitos, comandos, portas, credenciais
    de desenvolvimento e troubleshooting.
-   [ ] CI executa restore, build, testes e análise básica.
-   [ ] Nenhum segredo real está versionado.

## 20. Documentação obrigatória

Manter no repositório: - `README.md`: visão geral, arquitetura resumida,
pré-requisitos e execução. - `SPEC.md`: esta especificação. - C4 Context
e Container diagrams. - Diagramas de sequência de escrita, consumo e
consulta. - Diagrama de deployment local. - Mapa de domínios e
capacidades. - Requisitos funcionais e não funcionais. - Contratos de
eventos. - OpenAPI/Swagger. - ADRs. - Modelo de dados. - Estratégia de
segurança multiempresa. - Estratégia de resiliência. - Relatório de
testes de carga. - Procedimentos de operação, replay, backup e restore.

## 21. ADRs iniciais

Criar um arquivo para cada decisão: - ADR-001: Dois microsserviços em
vez de um monólito. - ADR-002: Event-driven para desacoplar lançamentos
e consolidação. - ADR-003: CQRS com projeção de leitura. - ADR-004:
PostgreSQL por serviço. - ADR-005: RabbitMQ + MassTransit. - ADR-006:
Transactional Outbox e idempotent consumer. - ADR-007: Multi-tenancy
lógica por MerchantId. - ADR-008: Consistência eventual. - ADR-009:
Redis como cache não autoritativo. - ADR-010: Estorno compensatório e
imutabilidade financeira.

Cada ADR deve conter: contexto, decisão, alternativas consideradas,
consequências positivas, trade-offs e riscos.

## 22. Instruções de execução local

O README deve fornecer comandos equivalentes a:

``` bash
# Restaurar e compilar
dotnet restore
dotnet build

# Executar testes
dotnet test

# Subir dependências e serviços
docker compose up --build -d

# Verificar containers
docker compose ps

# Consultar logs
docker compose logs -f cashflow-api
docker compose logs -f daily-consolidation-api

# Encerrar
docker compose down

# Encerrar e remover volumes (APAGA DADOS LOCAIS)
docker compose down -v
```

Swagger deve estar disponível em URLs locais documentadas, por
exemplo: - `http://localhost:<porta-cashflow>/swagger` -
`http://localhost:<porta-consolidation>/swagger`

As portas definitivas devem ser definidas uma única vez no Compose e
refletidas no README.

## 23. Instruções para Claude Code

Claude Code deve atuar como engenheiro responsável pela implementação
desta especificação.

### 23.1 Regras de trabalho

1.  Ler integralmente `SPEC.md` antes de alterar arquivos.
2.  Inspecionar o repositório e preservar código existente útil.
3.  Implementar incrementalmente, sem gerar todo o sistema em um único
    passo.
4.  Antes de adicionar pacote NuGet, verificar compatibilidade com .NET
    10 e escolher versão estável.
5.  Fixar versões em `Directory.Packages.props`.
6.  Executar build e testes após cada etapa.
7.  Não deixar `TODO`, métodos vazios, mocks no caminho de produção ou
    endpoints fictícios.
8.  Não afirmar que testes passaram sem executá-los.
9.  Não afirmar que o sistema atende 50 RPS sem executar teste de carga
    e guardar resultados.
10. Não introduzir Kubernetes, Kafka, Event Sourcing ou serviços extras
    sem justificativa e aprovação.
11. Não compartilhar entidades de domínio entre microsserviços.
12. Não permitir acesso cross-tenant.
13. Não usar `float`/`double` para valores monetários.
14. Não fazer chamadas HTTP síncronas do Cash Flow para a consolidação.
15. Não fazer retry cego de operações financeiras.
16. Manter documentação e diagramas atualizados com as decisões
    efetivamente implementadas.
17. Se houver conflito entre implementação e requisito, interromper,
    registrar a divergência e propor solução; não ignorar
    silenciosamente.

### 23.2 Ordem de implementação

**Fase 1 --- Bootstrap** - Criar solution e projetos. - Configurar .NET
10. - Configurar nullable reference types, analyzers e warnings. -
Configurar central package management. - Criar Docker Compose com
PostgreSQL, RabbitMQ e Redis. - Criar health checks básicos. - Criar
README inicial.

**Fase 2 --- Cash Flow Domain** - Implementar Merchant context,
CashAccount e CashTransaction. - Implementar regras de crédito, débito,
abertura e estorno. - Criar unit tests. - Implementar persistência EF
Core e migrations. - Implementar idempotência.

**Fase 3 --- Outbox e publicação** - Implementar gravação atômica de
lançamento e Outbox. - Implementar publisher em background. -
Implementar claim concorrente seguro. - Configurar RabbitMQ e
MassTransit. - Testar falhas entre commit, publish confirm e marcação da
Outbox.

**Fase 4 --- Consolidation** - Criar contratos de eventos versionados. -
Implementar consumer idempotente. - Implementar DailyBalances e
ProcessedEvents. - Implementar cálculo e recálculo de saldos. - Criar
API de consulta. - Testar eventos duplicados, fora de ordem e
retroativos.

**Fase 5 --- Multi-tenancy e segurança** - Implementar autenticação
JWT/OIDC ou mecanismo de desenvolvimento explicitamente isolado. -
Implementar resolução de tenant. - Implementar policies e autorização
por recurso. - Adicionar testes cross-tenant. - Considerar RLS como
defesa adicional.

**Fase 6 --- Cache, resiliência e observabilidade** - Implementar Redis
Cache-Aside. - Garantir fallback para PostgreSQL. - Configurar retries e
error queue. - Instrumentar OpenTelemetry. - Criar dashboards e alertas
locais.

**Fase 7 --- Qualidade e entrega** - Completar testes de integração,
contrato e resiliência. - Criar teste de carga de 50 RPS. - Criar CI. -
Finalizar diagramas, ADRs e documentação. - Revisar README do zero em
ambiente limpo.

### 23.3 Definição de pronto por fase

Uma fase só termina quando: - Código compila. - Testes relevantes
passam. - Migrations funcionam. - Contratos e documentação estão
atualizados. - Comandos de execução foram verificados. - Limitações
conhecidas estão registradas.

### 23.4 Primeira tarefa para Claude Code

Comece somente pela Fase 1.

Antes de codificar: 1. Apresente um plano curto de arquivos e projetos
que serão criados. 2. Verifique SDK .NET 10 disponível no ambiente. 3.
Verifique compatibilidade das versões de imagens Docker e pacotes. 4.
Crie a solution, projetos vazios, configurações comuns, Docker Compose e
README inicial. 5. Execute `dotnet restore`, `dotnet build` e
verificação do Docker Compose. 6. Relate os comandos executados,
resultados reais e problemas encontrados.

Não avance para a Fase 2 até que a Fase 1 esteja validada.

------------------------------------------------------------------------

## 24. Evoluções futuras

Após concluir o escopo: - Identity Provider dedicado e gestão avançada
de usuários. - Outbox/Inbox e replay operacional com trilha de
auditoria. - Reconciliação automática e alertas financeiros. -
Multi-moeda e regras cambiais. - Partidas dobradas (double-entry
ledger), se o domínio evoluir para contabilidade formal. - Relatórios
analíticos e exportação. - Sagas caso surjam processos distribuídos com
múltiplas etapas. - Deploy em cloud com banco gerenciado, broker
gerenciado, autoscaling e disaster recovery. - Estratégia de
particionamento por tenant e arquivamento de histórico. - CI/CD,
infraestrutura como código e políticas de segurança de supply chain.

## 25. Riscos e trade-offs conhecidos

  -----------------------------------------------------------------------
  Risco/trade-off                     Mitigação
  ----------------------------------- -----------------------------------
  Consistência eventual pode exibir   Expor `lastUpdatedAt`, lag e estado
  saldo temporariamente desatualizado da projeção

  Mensagens podem ser duplicadas      Idempotência transacional

  Eventos podem chegar fora de ordem  Agregação por data, recálculo e
                                      concorrência por conta

  Outbox pode crescer durante         Alertas, retenção e capacidade de
  indisponibilidade do broker         disco

  Cache pode ficar desatualizado      TTL/invalidação e PostgreSQL como
                                      fonte da projeção

  Multi-tenancy lógica exige          Resolução de tenant centralizada,
  disciplina                          constraints e testes

  Microsserviços aumentam             Limitar a dois serviços e
  complexidade operacional            automatizar ambiente local

  Recálculo retroativo pode ser caro  Medir, controlar concorrência e
                                      otimizar após evidência

  Quorum queues e HA aumentam custo   Usar configuração local simples;
  operacional                         desenhar HA para produção conforme
                                      SLO
  -----------------------------------------------------------------------

## 26. Resultado esperado

Ao final, o repositório público deve permitir que um avaliador: 1.
Entenda o negócio e os domínios. 2. Compreenda as decisões arquiteturais
e seus trade-offs. 3. Suba a aplicação localmente com Docker Compose. 4.
Explore as duas APIs pelo Swagger. 5. Registre créditos e débitos. 6.
Observe a consolidação assíncrona. 7. Desligue o consumidor e confirme
que lançamentos continuam funcionando. 8. Reinicie o consumidor e
confirme recuperação. 9. Verifique isolamento entre comerciantes. 10.
Execute os testes e consulte evidências de carga, resiliência e
observabilidade.

**Princípio orientador:** simplicidade na implementação inicial, rigor
na integridade financeira, isolamento entre comerciantes e resiliência
demonstrada por testes --- não apenas por diagramas.
