# Documentação do Projeto

## Arquitetura
| Documento | Conteúdo |
|---|---|
| [01 — Visão geral C4](architecture/01-visao-geral-c4.md) | Contexto, Containers, Componentes |
| [02 — Fluxos e sequência](architecture/02-fluxos-sequencia.md) | Escrita, Outbox, consumo, consulta, estorno, cenários de falha |
| [03 — Deployment e alta disponibilidade](architecture/03-deployment-alta-disponibilidade.md) | Docker Compose local; Kubernetes-alvo multi-AZ; DR |
| [04 — SOLID e Clean Architecture](architecture/04-solid-clean-architecture.md) | Estrutura de projetos, SOLID com exemplos, DDD tático |
| [05 — Arquitetura de transição](architecture/05-arquitetura-transicao.md) | Migração de legado (Strangler Fig + ACL + CDC) |
| [06 — Estimativa de custos](architecture/06-estimativa-custos.md) | Infraestrutura e licenças por perfil |
| [07 — Modelo de dados](architecture/07-modelo-dados.md) | DDL, constraints, recálculo, chaves de cache |
| [08 — Resiliência](architecture/08-resiliencia.md) | Políticas por camada, matriz de falhas, back-pressure |

## Negócio e requisitos
- [Mapa de domínios e capacidades](business/mapa-dominios-capacidades.md)
- [Requisitos funcionais e não funcionais](requirements/requisitos.md)

## Integração, segurança e operação
- [Contratos de eventos e topologia RabbitMQ](integration/contratos-eventos.md)
- [Segurança e multi-tenancy](security/seguranca-multiempresa.md)
- [Observabilidade](operations/observabilidade.md)
- [Runbooks (replay, reconstrução, backup/restore)](operations/runbooks.md)
- [Estratégia de testes e carga](testing/estrategia-testes.md)

## Decisões
- [ADRs](adr/README.md) (ADR-001 a ADR-013)
