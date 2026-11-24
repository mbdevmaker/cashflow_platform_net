# ADR-007 — Multi-tenancy lógica por MerchantId

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
A plataforma atende vários comerciantes. Vazamento de dados financeiros entre tenants é o risco de segurança mais grave do sistema.

## Decisão
Banco compartilhado por serviço com **`MerchantId` em todas as tabelas de negócio**, e **defesa em profundidade**:
1. Tenant resolvido exclusivamente de claims de token validado (`merchant_id`) + validação server-side de associação usuário-comerciante.
2. `ITenantContext` obrigatório em handlers e repositórios.
3. **FK composta** `(merchant_id, cash_account_id)`.
4. Filtros globais do EF Core (auxiliares).
5. **RLS** no PostgreSQL com `SET LOCAL app.merchant_id`.
6. Chaves de cache e eventos com `MerchantId`; consumidor valida.
7. `404` para recursos de outro tenant.
8. Testes negativos obrigatórios no CI.

## Alternativas consideradas
| Alternativa | Avaliação |
|---|---|
| Schema por tenant | Migrations × N; limite prático de centenas de tenants |
| Banco por tenant | Isolamento forte; custo e operação altos. Reservado para tier enterprise futuro |
| `MerchantId` via header/body | Inseguro — rejeitado |

## Consequências positivas
- Custo e operação baixos; escala para milhares de comerciantes.
- Isolamento garantido no banco mesmo com bug na aplicação (FK composta + RLS).

## Trade-offs
- *Noisy neighbor* → rate limit por tenant; particionamento por tenant como evolução.
- Exige disciplina → testes de arquitetura e testes negativos.

## Riscos
- RLS mal configurado com pooling → uso exclusivo de `SET LOCAL` (escopo de transação) e testes específicos.
