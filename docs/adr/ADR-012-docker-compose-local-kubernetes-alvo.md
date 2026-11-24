# ADR-012 — Docker Compose local; Kubernetes como plataforma-alvo

**Status:** Aceito · **Data:** 2026-11-23

## Contexto
O avaliador precisa subir a solução com um comando. O SPEC exclui Kubernetes da v1, mas a solução deve estar preparada para cloud com alta disponibilidade.

## Decisão
- **v1 / local:** Docker Compose com health checks, `depends_on: service_healthy`, redes internas, volumes nomeados, migrations como jobs one-shot, imagens multi-stage não-root.
- **Produção (desenho):** Kubernetes gerenciado (AKS/EKS/GKE) com réplicas multi-AZ, HPA, **KEDA** para consumers, PDBs, GitOps; ou PaaS de containers (Azure Container Apps / ECS Fargate / Cloud Run) se não houver time de plataforma.
- A aplicação segue **12-factor** desde já para que a migração seja de manifestos, não de código.

## Alternativas consideradas
| Alternativa | Avaliação |
|---|---|
| Kubernetes local (kind/minikube) na v1 | Aumenta a barreira para o avaliador sem benefício demonstrável |
| Apenas `dotnet run` + dependências manuais | Não reproduzível |
| .NET Aspire AppHost como orquestrador local | Boa experiência de dev; Compose é mais universal para avaliação e não exige SDK |

## Consequências positivas
- Execução local trivial e reproduzível; demonstração de falhas (`docker compose stop ...`).
- Caminho claro para produção (probes, config externa, workers separados, migrations como job).

## Trade-offs
- Compose não oferece HA real nem autoscaling — documentado como limitação.

## Riscos
- Divergência entre Compose e produção → mesmas imagens, mesmas variáveis, mesmos health endpoints; testes de resiliência executados nos dois.
