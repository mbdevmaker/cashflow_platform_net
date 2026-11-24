using System.Reflection;
using CashFlow.Application;
using CashFlow.Domain;
using CashFlow.Infrastructure;
using CashFlowPlatform.Contracts.Events;
using DailyConsolidation.Application;
using DailyConsolidation.Domain;
using NetArchTest.Rules;

namespace ArchitectureTests;

/// <summary>
/// Regras de dependência da Clean Architecture (RNF-13, seção 4 da especificação técnica).
/// Quebram o build quando uma camada interna passa a depender de uma externa ou de tecnologia concreta.
/// </summary>
public sealed class LayerDependencyTests
{
    private static readonly string[] Technology =
    [
        "Microsoft.EntityFrameworkCore", "Npgsql", "MassTransit", "RabbitMQ", "StackExchange.Redis", "Microsoft.AspNetCore"
    ];

    public static TheoryData<string, Assembly> ServiceAssemblies => new()
    {
        { "CashFlow", typeof(CashAccount).Assembly },
        { "CashFlow", typeof(CashFlowService).Assembly },
        { "CashFlow", typeof(DependencyInjection).Assembly },
        { "DailyConsolidation", typeof(DailyBalance).Assembly },
        { "DailyConsolidation", typeof(BalanceQueryService).Assembly },
        { "DailyConsolidation", typeof(DailyConsolidation.Infrastructure.DependencyInjection).Assembly }
    };

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} depende de [{string.Join(", ", forbidden)}] em: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void CashFlow_Domain_nao_depende_de_nenhuma_outra_camada_nem_de_tecnologia()
    {
        AssertNoDependency(typeof(CashAccount).Assembly,
            ["CashFlow.Application", "CashFlow.Infrastructure", "CashFlow.Api", "CashFlowPlatform", .. Technology]);
    }

    [Fact]
    public void Consolidation_Domain_nao_depende_de_nenhuma_outra_camada_nem_de_tecnologia()
    {
        AssertNoDependency(typeof(DailyBalance).Assembly,
            ["DailyConsolidation.Application", "DailyConsolidation.Infrastructure", "DailyConsolidation.Api", "CashFlowPlatform", .. Technology]);
    }

    [Fact]
    public void CashFlow_Application_nao_depende_de_infraestrutura_api_nem_tecnologia()
    {
        AssertNoDependency(typeof(CashFlowService).Assembly,
            ["CashFlow.Infrastructure", "CashFlow.Api", .. Technology]);
    }

    [Fact]
    public void Consolidation_Application_nao_depende_de_infraestrutura_api_nem_tecnologia()
    {
        AssertNoDependency(typeof(BalanceQueryService).Assembly,
            ["DailyConsolidation.Infrastructure", "DailyConsolidation.Api", .. Technology]);
    }

    [Fact]
    public void Infrastructure_nao_depende_da_camada_Api()
    {
        AssertNoDependency(typeof(DependencyInjection).Assembly, "CashFlow.Api");
        AssertNoDependency(typeof(DailyConsolidation.Infrastructure.DependencyInjection).Assembly, "DailyConsolidation.Api");
    }

    [Theory]
    [MemberData(nameof(ServiceAssemblies))]
    public void Servicos_nao_dependem_um_do_outro(string service, Assembly assembly)
    {
        var other = service == "CashFlow" ? "DailyConsolidation" : "CashFlow.";
        AssertNoDependency(assembly, other);
    }

    [Fact]
    public void Controle_a_regra_detecta_dependencia_quando_ela_existe()
    {
        // Garante que as regras acima não passam por engano (por exemplo, se nenhum tipo fosse carregado).
        var result = Types.InAssembly(typeof(DependencyInjection).Assembly)
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore").GetResult();
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public void Contrato_de_evento_nao_depende_de_nenhum_servico()
    {
        AssertNoDependency(typeof(CashTransactionCreatedV1).Assembly,
            ["CashFlow.", "DailyConsolidation", .. Technology]);
    }
}
