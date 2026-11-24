# ADR-009 — Redis como cache não autoritativo

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
A API de consolidação tem picos de 50 RPS, padrão de leitura repetitivo e meta de p95 ≤ 300 ms.

## Decisão
Redis (ou Valkey) como **cache-aside descartável** via `HybridCache` (L1 memória + L2 Redis, proteção contra stampede). Chaves com tenant e **versão por conta** (`INCR` pelo consumer após commit). TTL curto (60 s). Timeout de 50–100 ms + **circuit breaker** com fallback para PostgreSQL. Redis fora do readiness. Ativável/desativável por configuração.

## Alternativas consideradas
| Alternativa | Avaliação |
|---|---|
| Apenas `IMemoryCache` | Não compartilhado entre réplicas; invalidação inconsistente |
| Memcached | Sem estruturas/versão atômica rica; menor ecossistema .NET |
| **Valkey** | Compatível em protocolo, BSD-3; alternativa equivalente (sem mudança de código) |
| Sem cache, só réplica de leitura PG | Viável para 50 RPS; cache mantido como otimização e proteção de pico, a confirmar no teste de carga |

## Consequências positivas
- Menor latência, menos carga e conexões no PostgreSQL em picos.
- Falha do cache não causa falha da API.

## Trade-offs
- Possível dado com até 60 s de defasagem adicional se a invalidação falhar (mitigado pela versão + TTL e sinalizado pelo status de consistência lido junto com o dado).

## Riscos
- Vazamento entre tenants via chave mal formada → `ICacheKeyStrategy` única, testada, com `MerchantId` obrigatório.
- Licença Redis (RSAL/SSPL/AGPL) → sem impacto para uso interno; Valkey como saída.
