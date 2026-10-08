namespace Civil3D2026Plugin.Infrastructure;

/// <summary>
/// Fonte unica para os nomes publicos dos comandos.
///
/// Para renomear um comando no futuro, altere SOMENTE a constante correspondente
/// neste arquivo. Os CommandMethod e o catalogo de comandos usam estas constantes.
/// </summary>
public static class CommandNames
{
    public static class Core
    {
        public const string Help = "C3DHELP";
        public const string Ribbon = "C3DRIBBON";
        public const string RibbonReset = "C3DRIBBONRESET";
    }

    public static class Drenagem
    {
        public const string Excel = "DRENEXCEL";
        public const string ExcelPreview = "DRENEXCELPREVIEW";
        public const string ExcelHelp = "DRENEXCELHELP";

        public const string Num = "DRENNUM";
        public const string NumPreview = "DRENNUMPREVIEW";
        public const string NumHelp = "DRENNUMHELP";
    }

    public static class Qto
    {
        public const string Estudo = "QTOESTUDO";
        public const string GeoTeste = "QTOGEOTESTE";
        public const string Help = "QTOHELP";
        public const string Teste = "TESTEQTO";
    }

    public static class Corridor
    {
        public const string Split = "CORRSPLIT";
        public const string MfRebaixo = "MFREBAIXO";
        public const string MfRebaixoCfg = "MFREBAIXOCFG";
        public const string MfRebaixoHelp = "MFREBAIXOHELP";

        public const string SolidArray = "C3DSOLIDARRAY";
        public const string SolidCfg = "C3DSOLIDCFG";
        public const string SolidAtualizar = "C3DSOLIDATUALIZAR";
        public const string SolidEditar = "C3DSOLIDEDITAR";
        public const string SolidItem = "C3DSOLIDITEM";
        public const string SolidHelp = "C3DSOLIDHELP";

        public const string Passagem = "PASSAGEM";
        public const string PassagemCfg = "PASSAGEMCFG";
        public const string PassagemEditar = "PASSAGEMEDITAR";
        public const string PassagemGrupo = "PASSAGEMGRUPO";
        public const string PassagemAtualizar = "PASSAGEMATUALIZAR";
        public const string PassagemHelp = "PASSAGEMHELP";
    }

    public static class FeatureLines
    {
        public const string ZSet = "FLZSET";
        public const string Descarregar = "FLDESCARREGAR";
        public const string SetPv = "FLSETPV";
        public const string RenomearCorte = "FLRENOMEARCORTE";
    }

    public static class Superficies
    {
        public const string ColorAuto = "SURFCOLORAUTO";
        public const string ReadScheme = "SURFREADSCHEME";
        public const string SlopeFilter = "SFSLOPEFILTER";
        public const string TrimLines = "TRIMSURFLINES";
        public const string Cortar1Mm = "CORTAR1MM";
    }

    public static class Geometria
    {
        public const string PointNormalDebug = "PTNORMALDBG";
    }

    public static class Diagnostico
    {
        public const string TesteDll = "TESTEDLL";
    }
}
