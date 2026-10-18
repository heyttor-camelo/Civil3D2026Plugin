using System;
using System.Collections.Generic;

namespace Civil3D2026Plugin.Infrastructure;

/// <summary>Quem mostra o progresso de uma operacao longa (janela de progresso das janelas do plugin).</summary>
internal interface IProgressoVisor
{
    /// <summary>Etapa atual e fracao do total (0 a 1). Chamado na thread do AutoCAD.</summary>
    void Mostrar(string etapa, double fracao);

    /// <summary>O usuario pediu para parar (lido so nos pontos seguros da operacao).</summary>
    bool Cancelado { get; }

    void Fechar();
}

/// <summary>
/// Progresso das operacoes longas de qualquer modulo. O codigo chama Mostrar/Passo/Faixa sem
/// saber quem mostra; sem visor ativo nada acontece (comandos, atualizacao automatica).
/// Faixa divide o total em etapas (ex.: 0-10% frames, 10-60% sheets) e pode ser aninhada.
/// </summary>
internal static class Progresso
{
    private static IProgressoVisor? _visor;
    private static readonly Stack<(double De, double Ate)> Faixas = new();
    private static double _de, _ate = 1;
    private static string _ultima = "";

    public static bool Ativo => _visor is not null;

    /// <summary>O usuario clicou em Cancelar (a operacao decide onde para).</summary>
    public static bool Cancelado => _visor?.Cancelado == true;

    /// <summary>Liga o visor ate o Dispose. Se ja ha um ativo (operacao dentro de outra), fica o de fora.</summary>
    public static IDisposable Iniciar(IProgressoVisor visor)
    {
        if (_visor is not null)
        {
            visor.Fechar();
            return new Acao(() => { });
        }
        _visor = visor;
        Faixas.Clear();
        _de = 0;
        _ate = 1;
        _ultima = "";
        return new Acao(() =>
        {
            IProgressoVisor? v = _visor;
            _visor = null;
            Faixas.Clear();
            _de = 0;
            _ate = 1;
            try
            {
                v?.Fechar();
            }
            catch (Exception)
            {
            }
        });
    }

    /// <summary>Etapa com a fracao dentro da faixa atual (0 a 1; NaN mantem a posicao).</summary>
    public static void Mostrar(string etapa, double fracao = double.NaN)
    {
        if (_visor is null)
            return;
        _ultima = etapa;
        double f = double.IsNaN(fracao) ? double.NaN : _de + (_ate - _de) * Math.Clamp(fracao, 0, 1);
        try
        {
            _visor.Mostrar(etapa, f);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Item i (0-based) de n dentro da faixa atual.</summary>
    public static void Passo(string etapa, int i, int n) => Mostrar(etapa, n > 0 ? (double)i / n : 0);

    /// <summary>Enquanto vivo, 0-1 passa a valer de..ate da faixa atual.</summary>
    public static IDisposable Faixa(double de, double ate)
    {
        if (_visor is null)
            return new Acao(() => { });
        Faixas.Push((_de, _ate));
        double largura = _ate - _de;
        double novoDe = _de + largura * Math.Clamp(de, 0, 1);
        double novoAte = _de + largura * Math.Clamp(ate, 0, 1);
        _de = novoDe;
        _ate = Math.Max(novoDe, novoAte);
        return new Acao(() =>
        {
            // Fim da etapa: a barra vai para o fim da faixa dela.
            if (_visor is not null)
                Mostrar(_ultima, 1);
            if (Faixas.Count > 0)
                (_de, _ate) = Faixas.Pop();
        });
    }

    private sealed class Acao : IDisposable
    {
        private Action? _acao;

        public Acao(Action acao) => _acao = acao;

        public void Dispose()
        {
            Action? a = _acao;
            _acao = null;
            a?.Invoke();
        }
    }
}
