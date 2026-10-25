using System;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Civil3D2026Plugin.Infrastructure;

namespace Civil3D2026Plugin.Modules.Layouts;

/// <summary>
/// Ligada/desligada de verdade. Viewport.On devolve false para viewports de layouts que ainda nao
/// foram abertos na sessao (ou que passam do MAXACTVP), mesmo ligadas no desenho. O que vale e o
/// bit 0x20000 ("viewport off") das flags DXF 90, lido pelo acdbEntGet.
/// </summary>
internal static class ViewportStatus
{
    private const int BitDesligada = 0x20000;

    [StructLayout(LayoutKind.Sequential)]
    private struct AdsName
    {
        public long A;
        public long B;
    }

    [DllImport("acdb25.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "?acdbGetAdsName@@YA?AW4ErrorStatus@Acad@@AEAY01_JVAcDbObjectId@@@Z")]
    private static extern ErrorStatus AdsName25(out AdsName nome, ObjectId id);

    [DllImport("acdb26.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "?acdbGetAdsName@@YA?AW4ErrorStatus@Acad@@AEAY01_JVAcDbObjectId@@@Z")]
    private static extern ErrorStatus AdsName26(out AdsName nome, ObjectId id);

    [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acdbEntGet")]
    private static extern IntPtr EntGet(ref AdsName nome);

    private static int _versao = 25;
    private static readonly System.Collections.Generic.HashSet<long> Avisadas = new();
    private static bool _indisponivel;

    /// <summary>true = ligada. Sem como ler as flags, fica o Viewport.On.</summary>
    public static bool Ligada(Viewport vp)
    {
        if (vp.On)
            return true;
        int? flags = Flags(vp.ObjectId);
        if (flags is not int f)
            return false;
        bool ligada = (f & BitDesligada) == 0;
        if (ligada && Avisadas.Add(vp.Handle.Value))
            PluginLog.Diag("Layouts", $"Viewport {vp.Handle}: On=false pela API, mas ligada no desenho (flags 90 = 0x{f:X}).");
        return ligada;
    }

    private static int? Flags(ObjectId id)
    {
        if (_indisponivel)
            return null;
        try
        {
            AdsName nome;
            ErrorStatus es;
            try
            {
                es = _versao == 25 ? AdsName25(out nome, id) : AdsName26(out nome, id);
            }
            catch (System.Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                if (_versao == 26)
                    throw;
                _versao = 26;
                es = AdsName26(out nome, id);
            }
            if (es != ErrorStatus.OK)
                return null;
            IntPtr p = EntGet(ref nome);
            if (p == IntPtr.Zero)
                return null;
            using var rb = (ResultBuffer)DisposableWrapper.Create(typeof(ResultBuffer), p, true);
            foreach (TypedValue tv in rb)
                if (tv.TypeCode == 90)
                    return Convert.ToInt32(tv.Value);
            return null;
        }
        catch (System.Exception ex)
        {
            _indisponivel = true;
            PluginLog.Aviso("Layouts", "Nao foi possivel ler as flags das viewports (fica o Viewport.On): " + ex.Message);
            return null;
        }
    }
}
