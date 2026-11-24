# Estimativa de Custos — Infraestrutura e Licenças

> **Requisito diferencial do desafio.**
> **Aviso:** valores **aproximados**, em USD/mês, baseados em preços públicos *on-demand* de referência (região us-east-1 / East US), arredondados. **Regiões no Brasil (São Paulo) costumam ser ~30–60% mais caras.** Preços mudam; os números devem ser confirmados nas calculadoras oficiais (AWS Pricing Calculator, Azure Pricing Calculator) antes de qualquer decisão. O objetivo aqui é mostrar **ordem de grandeza** e **onde estão os custos**, não um orçamento fechado.

## 1. Premissas de dimensionamento

| Premissa | Valor |
|---|---|
| Consultas de consolidação | 50 RPS em pico; ~5 RPS médio |
| Lançamentos | ~10 RPS em pico; ~1–2 milhões/mês |
| Dados | ~1 KB por lançamento → ~2 GB/ano no Cash Flow + índices; projeção bem menor |
| Retenção de telemetria | 15 dias métricas detalhadas, 7 dias traces (amostrados), 30 dias logs |
| Disponibilidade-alvo | 99,9–99,95% (multi-AZ) |

**Observação importante para a defesa:** a carga é **pequena**. O custo é dominado pelos **requisitos de alta disponibilidade** (múltiplas zonas, standby, cluster de broker), não pelo volume. Por isso há três perfis.

## 2. Perfis de ambiente

### Perfil A — Desenvolvimento / Demonstração (Docker Compose)

| Item | Custo |
|---|---|
| Máquina do desenvolvedor ou 1 VM (4 vCPU / 16 GB) | US$ 0 (local) a ~US$ 120 (VM cloud) |
| Licenças | US$ 0 |
| **Total** | **US$ 0 – 120** |

### Perfil B — Produção enxuta em PaaS (sem time de plataforma)

Exemplo em Azure (equivalente em AWS: ECS Fargate + RDS + CloudAMQP/Amazon MQ + ElastiCache).

| Componente | Serviço | Aproximação |
|---|---|---|
| 4 apps (2 APIs, publisher, consumer) | Azure Container Apps (consumo, min 1–2 réplicas) | 80 – 200 |
| PostgreSQL Cash Flow | Flexible Server General Purpose D2ds + **HA zone-redundant** | 250 – 300 |
| PostgreSQL Consolidation | Flexible Server Burstable B2s (sem HA; projeção reconstruível) | 30 – 50 |
| RabbitMQ | CloudAMQP (plano dedicado pequeno, 1–3 nós) | 100 – 500 |
| Redis | Azure Cache for Redis Basic/Standard C0–C1 (ou Valkey gerenciado) | 15 – 100 |
| Gateway / TLS | Ingress do Container Apps / Front Door básico | 0 – 50 |
| Observabilidade | Grafana Cloud (tier com uso) ou Azure Monitor com amostragem | 50 – 200 |
| Key Vault, registry, backup, rede | — | 20 – 60 |
| **Total** | | **≈ US$ 550 – 1.450 / mês** |

### Perfil C — Produção HA em Kubernetes (arquitetura-alvo)

Exemplo em AWS.

| Componente | Serviço / dimensionamento | Aproximação |
|---|---|---|
| Control plane | EKS | 75 |
| Nós de aplicação | 3× m6g.large (2 vCPU / 8 GB), 1 por AZ | 170 |
| PostgreSQL Cash Flow | RDS db.m6g.large **Multi-AZ** + 100 GB gp3 + backups | 250 – 300 |
| PostgreSQL Consolidation | RDS db.t4g.medium Multi-AZ + réplica de leitura | 150 – 200 |
| RabbitMQ — opção 1 | **Amazon MQ** cluster 3× mq.m5.large | 600 – 700 |
| RabbitMQ — opção 2 | RabbitMQ Cluster Operator em 3 nós dedicados (t4g.large) | 150 – 200 |
| Redis/Valkey | ElastiCache cache.t4g.small primário + réplica (Valkey é mais barato) | 40 – 60 |
| Load Balancer + WAF | ALB + AWS WAF | 40 – 70 |
| NAT Gateway | 1–3 (custo + tráfego) | 35 – 110 |
| Observabilidade | Grafana Cloud / Amazon Managed Prometheus + Grafana / self-hosted | 100 – 300 |
| Secrets, ECR, KMS, logs de auditoria | — | 20 – 50 |
| **Total com Amazon MQ** | | **≈ US$ 1.500 – 2.100 / mês** |
| **Total com RabbitMQ self-hosted** | | **≈ US$ 1.050 – 1.600 / mês** |

**Otimizações conhecidas:** Savings Plans / Reserved Instances (−30 a −45% em compute e RDS com compromisso de 1–3 anos), instâncias ARM (Graviton/Ampere), Valkey no lugar de Redis, amostragem de traces no Collector (*tail sampling*), consolidar ambientes não produtivos com escala a zero fora do horário.

## 3. Licenças de software

| Componente | Licença | Custo de licença | Observação |
|---|---|---|---|
| .NET 10 / ASP.NET Core / EF Core | MIT | 0 | — |
| Npgsql | PostgreSQL License | 0 | — |
| PostgreSQL | PostgreSQL License | 0 | Custo é do serviço gerenciado |
| RabbitMQ | MPL 2.0 | 0 | Suporte comercial opcional (Broadcom/Tanzu) |
| **MassTransit v8** | Apache 2.0 | 0 | Suporte da v8 por tempo limitado |
| **MassTransit v9** | **Comercial** | Pago (verificar tabela vigente) | Ver ADR-005: decidir entre pagar ou migrar |
| Wolverine / Rebus (alternativas) | MIT | 0 | Rotas de saída |
| Redis 8 | AGPLv3 / RSALv2 / SSPLv1 | 0 para uso interno como cache | Uso via serviço gerenciado não gera obrigação adicional prática |
| Valkey | BSD-3 | 0 | Alternativa compatível |
| OpenTelemetry (SDK + Collector) | Apache 2.0 | 0 | — |
| Prometheus, Jaeger | Apache 2.0 | 0 | — |
| Grafana, Loki, Tempo | AGPLv3 | 0 self-hosted | Grafana Cloud é pago por uso |
| Kubernetes, KEDA, Argo CD | Apache 2.0 | 0 | — |
| Debezium (transição) | Apache 2.0 | 0 | — |
| xUnit, Testcontainers, k6, NBomber | Apache/MIT/AGPL | 0 | NBomber tem licença comercial para uso corporativo — preferir **k6** |
| MediatR v13+, FluentAssertions v8+ | Comerciais | Evitados | Substituídos por abstrações próprias / asserts nativos / Shouldly |

**Total de licenças na configuração recomendada: US$ 0.** O risco de licenciamento está mapeado (MassTransit v9) e tem estratégia de saída.

## 4. Custos que não aparecem na fatura de cloud

| Item | Comentário |
|---|---|
| Operação de Kubernetes | Upgrades, segurança e plataforma exigem ~0,5–1 FTE se não houver time de plataforma → argumento a favor do Perfil B |
| Operação de RabbitMQ self-hosted | Upgrades, monitoramento de quorum e alarmes → argumento a favor do gerenciado quando não houver expertise |
| Plantão/on-call | Necessário para SLO 99,9%+ |
| Testes de carga/caos periódicos | Ambiente efêmero (horas/mês) |

## 5. Recomendação

1. **Começar com Perfil B** (PaaS + gerenciados) se a organização não tiver plataforma Kubernetes madura — custo total de propriedade (TCO) menor.
2. **Perfil C** quando houver plataforma K8s corporativa compartilhada (o custo marginal cai muito, porque cluster, observabilidade e GitOps já existem).
3. Revisar dimensionamento após 30 dias de métricas reais de produção (right-sizing).
