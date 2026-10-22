using System;
using System.Linq;
using System.Reflection;

namespace Civil3D2026Plugin.Modules.Hydro;

/// <summary>
/// Propriedades da bacia (Catchment) que mudam entre os updates do Civil 3D 2026: no 2026.1
/// (13.8) nao existem ReferenceDischargeObjectId nem FlowPathLength na API. Acessadas por
/// reflexao para a mesma DLL compilar e rodar em qualquer update; sem a propriedade, a leitura
/// devolve nulo/NaN e a gravacao avisa.
/// </summary>
internal static class CatchmentApi
{
    private static readonly string[] DischargeNames = { "ReferenceDischargeObjectId", "ReferenceDischargeId", "DischargeObjectId", "ReferencePipeNetworkStructureId" };
    private static readonly string[] LengthNames = { "FlowPathLength", "HydrologicallyMostDistantLength", "FlowLength" };

    private static PropertyInfo? Find(string[] names) =>
        names.Select(n => typeof(CivilDb.Catchment).GetProperty(n, BindingFlags.Public | BindingFlags.Instance)).FirstOrDefault(p => p is not null);

    private static readonly PropertyInfo? Discharge = Find(DischargeNames);
    private static readonly PropertyInfo? Length = Find(LengthNames);

    /// <summary>Se este Civil liga a bacia a uma estrutura pela API.</summary>
    public static bool HasDischarge => Discharge is not null;

    public static AcadDb.ObjectId GetDischarge(CivilDb.Catchment c)
    {
        try
        {
            return Discharge?.GetValue(c) is AcadDb.ObjectId id ? id : AcadDb.ObjectId.Null;
        }
        catch
        {
            return AcadDb.ObjectId.Null;
        }
    }

    public static void SetDischarge(CivilDb.Catchment c, AcadDb.ObjectId id)
    {
        if (Discharge is null || !Discharge.CanWrite)
            throw new InvalidOperationException("Esta versao do Civil 3D nao permite ligar a bacia a estrutura pela API (atualize o Civil 3D 2026).");
        Discharge.SetValue(c, id);
    }

    public static double GetFlowPathLength(CivilDb.Catchment c)
    {
        try
        {
            return Length?.GetValue(c) is double d ? d : double.NaN;
        }
        catch
        {
            return double.NaN;
        }
    }
}
