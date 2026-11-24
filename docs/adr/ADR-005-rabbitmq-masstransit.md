# ADR-005 — RabbitMQ + MassTransit

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
É preciso entregar eventos financeiros de forma confiável (at-least-once), com retry, dead-letter, back-pressure e rastreabilidade, rodando localmente e em cloud.

## Decisão
- **Broker:** RabbitMQ 4.x, exchange de eventos, filas **duráveis**, **quorum queues** em produção (3 nós), mensagens persistentes, **publisher confirms**, DLQ.
- **Biblioteca:** MassTransit (v8, Apache 2.0, versão fixada em `Directory.Packages.props`), **restrita à camada Infrastructure**, atrás de portas próprias (`IIntegrationEventPublisher` e handlers de Application).

## Alternativas consideradas — broker
| Alternativa | Motivo de não escolher agora |
|---|---|
| Apache Kafka | Otimizado para alto throughput/replay por offset; sem ACK por mensagem/DLQ nativos; mais pesado para operar; custo alto para baixo volume |
| Azure Service Bus | Excelente, mas lock-in e sem execução local fiel |
| Amazon SQS/SNS | Excelente custo, mas lock-in e emulação local parcial |
| Redis Streams | Durabilidade/HA inferiores para dados financeiros |

## Alternativas consideradas — biblioteca
| Alternativa | Avaliação |
|---|---|
| `RabbitMQ.Client` puro | Reimplementar reconexão, confirms, retry, DLQ, tracing — alto risco de bugs |
| NServiceBus | Robusto, licença comercial |
| **Wolverine** (MIT) | Boa alternativa; plano B em caso de troca |
| **Rebus** (MIT) | Simples e estável; plano B |

## Consequências positivas
- Retry em camadas, fila `_error`, topologia automática, OpenTelemetry nativo, test harness.
- Portabilidade multicloud (RabbitMQ gerenciado ou operador K8s).

## Trade-offs
- Sem ordenação garantida com consumidores concorrentes (tratado no ADR-013).
- Sem replay histórico nativo (RabbitMQ Streams ou Kafka em evolução, se necessário).

## Riscos
- **Licenciamento:** MassTransit v9 é comercial; v8 tem suporte por tempo limitado. **Mitigação:** uso isolado em Infrastructure; Outbox própria (não dependente do MassTransit); decisão formal *pagar v9 × migrar para Wolverine/Rebus* antes do fim do suporte da v8. Verificar termos vigentes no momento da implantação.
- Operação de cluster RabbitMQ → preferir gerenciado (Amazon MQ, CloudAMQP) ou operador oficial.

## Quando reavaliar
Volume sustentado muito alto (dezenas de milhares de eventos/s), necessidade de replay histórico por múltiplos consumidores, ou padrão corporativo em Kafka.
