#!/usr/bin/env python3
"""Biblioteca para gerar pacotes .pkt do Subassembly Composer (extraida do gen_mf.py).
Uso: importar os helpers (var, pt, pt_slope, lk, sh, out, Switch), montar STEPS e chamar write(...)."""
import sys, uuid, zipfile
from xml.sax.saxutils import escape

def q(v):  # escape para atributo
    return escape(v, {'"': '&quot;'})

# preenchidos pelo gerador antes de chamar write()
EXPR = {}
VARTYPES = {}

# ----------------------------------------------------------------- passos
STEPS = []
def var(name):            STEPS.append(dict(kind="var", name=name))
def pt(num, frm, dx, dy, codes=(), link=None, lcodes=(), disp=None):
    STEPS.append(dict(kind="pt", num=num, frm=frm, dx=dx, dy=dy, codes=list(codes),
                      link=link, lcodes=list(lcodes), disp=disp))
def pt_slope(num, frm, slope, dx, elevtarget, codes=(), link=None, lcodes=(), disp=None, ot=""):
    STEPS.append(dict(kind="ptslope", num=num, frm=frm, slope=slope, dx=dx, et=elevtarget, ot=ot,
                      codes=list(codes), link=link, lcodes=list(lcodes), disp=disp))
def lk(num, a, b, codes=(), disp=None): STEPS.append(dict(kind="lk", num=num, a=a, b=b, codes=list(codes), disp=disp))
def sh(num, links, codes=(), disp=None): STEPS.append(dict(kind="sh", num=num, links=links, codes=list(codes), disp=disp))
def out(name, expr): STEPS.append(dict(kind="out", name=name, expr=expr))
class Switch:
    """bloco condicional multi-caso: with Switch(expr) as sw: / with sw.case("A"): ..."""
    def __init__(self, expr, disp=""):
        self.expr, self.disp, self.cases = expr, disp, []
    def __enter__(self): return self
    def __exit__(self, *a): STEPS.append(dict(kind="switch", sw=self))
    def case(self, key):
        sw = self
        class _C:
            def __enter__(self2):
                global STEPS; self2._p = STEPS; STEPS = []; return self2
            def __exit__(self2, *a):
                global STEPS; steps = STEPS; STEPS = self2._p; sw.cases.append((key, steps))
        return _C()

# ----------------------------------------------------------------- XAML
VS_EXP = ('<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
          '<x:Boolean x:Key="IsExpanded">True</x:Boolean></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
COMMON = 'Geometry="[Geometry]" sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]"'

def codelist(tag, codes):
    if not codes: return ""
    items = "".join(f'<InArgument x:TypeArguments="x:String">{escape(c)}</InArgument>' for c in codes)
    return f'<{tag}><scg:List x:TypeArguments="InArgument(x:String)" Capacity="4">{items}</scg:List></{tag}>'

aid = [0]
def nextid():
    aid[0] += 1; return aid[0]

def activity(st):
    k = st["kind"]
    if k == "var":
        n = st["name"]; t = VARTYPES[n]; tx = "x:Double" if t == "Double" else "x:String"
        return (f'<asa1:InternalVariableDefine ActivityId="{nextid()}" DisplayName="{q(n)} &lt;{t}&gt;" '
                f'sap:VirtualizedContainerService.HintSize="200,22" OldVariableName="" ShowErrors="True" '
                f'SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]" '
                f'VariableName="{n}" VariableType="{t}">'
                f'<asa1:InternalVariableDefine.DefaultValue><InArgument x:TypeArguments="{tx}">{escape(EXPR[n])}</InArgument></asa1:InternalVariableDefine.DefaultValue>'
                f'<asa1:InternalVariableDefine.Variable><OutArgument x:TypeArguments="{tx}">[{n}]</OutArgument></asa1:InternalVariableDefine.Variable>'
                f'{VS_EXP}</asa1:InternalVariableDefine>')
    if k in ("pt", "ptslope"):
        disp = st["disp"] or st["num"]
        auto = st["link"] is not None
        attrs = (f'ActivityId="{nextid()}" ApplyAOR="False" AutoLink="{auto}" '
                 + (f'AutoLinkGeometryName="{st["link"]}" ' if auto else 'AutoLinkCodes="{x:Null}" AutoLinkGeometryName="{x:Null}" ')
                 + f'DisplayName="{q(disp)}" '
                 + (f'FromPoint="{st["frm"]}" ' if st["frm"] else 'FromPoint="{x:Null}" ')
                 + (('PointCodes="{x:Null}" ') if not st["codes"] else '')
                 + f'{COMMON} PointNumber="{st["num"]}" Positioning="{"SlopeAndDeltaX" if k=="ptslope" else "DeltaXAndDeltaY"}" Side="[Side]"')
        if k == "pt":
            args = (f'<InArgument x:TypeArguments="x:Double" x:Key="DeltaX1">{escape(st["dx"])}</InArgument>'
                    f'<InArgument x:TypeArguments="x:Double" x:Key="DeltaY1">{escape(st["dy"])}</InArgument>')
        else:
            OTX = (f'<InArgument x:TypeArguments="asw:OffsetTarget" x:Key="OffsetTarget2">{escape(st["ot"])}</InArgument>'
                   if st["ot"] else '<InArgument x:TypeArguments="asw:OffsetTarget" x:Key="OffsetTarget2" />')
            args = (f'<InArgument x:TypeArguments="x:Double" x:Key="Slope2">{escape(st["slope"])}</InArgument>'
                    f'<InArgument x:TypeArguments="x:Double" x:Key="DeltaX2">{escape(st["dx"])}</InArgument>'
                    + OTX +
                    f'<InArgument x:TypeArguments="asw:ElevationTarget" x:Key="ElevationTarget2">{escape(st["et"])}</InArgument>'
                    f'<InArgument x:TypeArguments="asw:CrossSegmentType" x:Key="Superelevation2" />')
        body = f'<asa1:CreatePoint.Arguments>{args}</asa1:CreatePoint.Arguments>'
        if auto: body += codelist("asa1:CreatePoint.AutoLinkCodes", st["lcodes"])
        body += codelist("asa1:CreatePoint.PointCodes", st["codes"])
        return f'<asa1:CreatePoint {attrs}>{body}{VS_EXP}</asa1:CreatePoint>'
    if k == "lk":
        disp = st["disp"] or st["num"]
        return (f'<asa1:CreateLink ActivityId="{nextid()}" ApplyAOR="False" DisplayName="{q(disp)}" EndPoint="{st["b"]}" '
                f'{COMMON} IsEnabled="True" LinkNumber="{st["num"]}" StartPoint="{st["a"]}">'
                f'{codelist("asa1:CreateLink.LinkCodes", st["codes"])}{VS_EXP}</asa1:CreateLink>')
    if k == "sh":
        disp = st["disp"] or st["num"]
        comp = "".join(f"<x:String>{l}</x:String>" for l in st["links"])
        return (f'<asa1:CreateShape ActivityId="{nextid()}" DisplayName="{q(disp)}" {COMMON} Links="{",".join(st["links"])}" ShapeNumber="{st["num"]}">'
                f'<asa1:CreateShape.ComponentNames>{comp}</asa1:CreateShape.ComponentNames>'
                f'{codelist("asa1:CreateShape.ShapeCodes", st["codes"])}{VS_EXP}</asa1:CreateShape>')
    if k == "out":
        return (f'<asa1:SetOutputParameterValue ActivityId="{nextid()}" DisplayName="{q(st["name"]+" = "+st["expr"])}" '
                f'sap:VirtualizedContainerService.HintSize="200,22" ShowErrors="True" SubassemblyErrorCenter="[SubassemblyErrorCenter]" SubassemblyRunMode="[SubassemblyRunMode]">'
                f'<asa1:SetOutputParameterValue.OutputParammeter><OutArgument x:TypeArguments="x:Double">[{st["name"]}]</OutArgument></asa1:SetOutputParameterValue.OutputParammeter>'
                f'<asa1:SetOutputParameterValue.Value><InArgument x:TypeArguments="x:Double">{escape(st["expr"])}</InArgument></asa1:SetOutputParameterValue.Value>'
                f'{VS_EXP}</asa1:SetOutputParameterValue>')
    raise ValueError(k)

# cadeia de FlowSteps aninhados (com FlowSwitch para blocos condicionais)
NAMES = []
ypos = [29]
def vs_step(x, y, last):
    con = '' if last else f'<av:PointCollection x:Key="ConnectorLocation">{x+100},{y+22} {x+100},{y+50}</av:PointCollection>'
    return (f'<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
            f'<av:Point x:Key="ShapeLocation">{x},{y}</av:Point><av:Size x:Key="ShapeSize">200,22</av:Size>{con}'
            f'</scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
def build(steps, cont, x):
    """steps -> XAML de cadeia aninhada. cont = None | ("define", name, xml) | ("ref", name)."""
    if not steps:
        if not cont: return ''
        return cont[2] if cont[0] == "define" else f'<x:Reference>{cont[1]}</x:Reference>'
    st = steps[0]; rest = steps[1:]
    if st["kind"] == "switch":
        sw = st["sw"]
        swname = f"__ReferenceID{len(NAMES)+1}"; NAMES.append(swname)
        y = ypos[0]; ypos[0] += 100
        if rest:
            rest_xml = build(rest, cont, x)
            rest_name = rest_xml.split('x:Name="', 1)[1].split('"', 1)[0]
            cont2 = ("define", rest_name, rest_xml)
        else:
            cont2 = cont
        cases_xml = []
        for i, (key, csteps) in enumerate(sw.cases):
            assert csteps, f"caso vazio {key}"
            c = cont2 if i == 0 else (("ref", cont2[1]) if cont2 else None)
            cx = build(csteps, c, x + 260 * (i + 1))
            tag = cx.split(' ', 1)[0]          # elemento raiz do caso (<FlowStep ou <FlowSwitch)
            assert tag in ('<FlowStep', '<FlowSwitch'), tag
            cases_xml.append(tag + f' x:Key="{key}"' + cx[len(tag):])
        cont_name = cont2[1] if cont2 else None
        default = f' Default="{{x:Reference {cont_name}}}"' if cont_name else ''
        vs = (f'<sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">'
              f'<x:Boolean x:Key="IsExpanded">True</x:Boolean><av:Point x:Key="ShapeLocation">{x+65},{y}</av:Point>'
              f'<av:Size x:Key="ShapeSize">70,87</av:Size></scg:Dictionary></sap:WorkflowViewStateService.ViewState>')
        return (f'<FlowSwitch x:TypeArguments="x:String"{default} x:Name="{swname}" Expression="{q(sw.expr)}" '
                f'sap:VirtualizedContainerService.HintSize="70,87">{vs}{"".join(cases_xml)}</FlowSwitch>')
    name = f"__ReferenceID{len(NAMES)+1}"; NAMES.append(name)
    y = ypos[0]; ypos[0] += 50
    nxt_xml = build(rest, cont, x)
    last = not nxt_xml
    nxt = f'<FlowStep.Next>{nxt_xml}</FlowStep.Next>' if nxt_xml else ''
    return f'<FlowStep x:Name="{name}">{vs_step(x, y, last)}{activity(st)}{nxt}</FlowStep>'


def write(OUT, NAME, VERSION, PARAMS, TARGETS, OUTPUTS, ENUM_GROUPS):
    sys.setrecursionlimit(10000)
    NAMES.clear(); ypos[0] = 29; aid[0] = 0
    GUID = uuid.uuid4().hex
    chain = build(STEPS, None, 40)
    refs = "".join(f"<x:Reference>{n}</x:Reference>" for n in NAMES)

    # atributos raiz (defaults)
    rootattrs = []
    for n, d, pv, on, tk in TARGETS:
        cls = "PreviewOffsetTarget" if tk == "offset" else "PreviewElevationTarget"
        rootattrs.append(f'this:Subassembly.{n}="[new {cls}(&quot;{n}&quot;, {on}, {pv})]"')
    rootattrs.append('this:Subassembly.Side="[new EnumType(0, &quot;Right&quot;, &quot;Side&quot;)]"')
    for n, t, d, disp in PARAMS:
        if t in ("double", "string"): rootattrs.append(f'this:Subassembly.{n}="{q(d)}"')
        elif t == "grade": rootattrs.append(f'this:Subassembly.{n}="[new Grade({d})]"')
        elif t == "enumYN": rootattrs.append(f'this:Subassembly.{n}="[new EnumType({10 if d=="Yes" else 11}, &quot;{d}&quot;)]"')
        elif t.startswith("enum:"):
            g = t[5:]; key = dict((i[0], i[2]) for i in ENUM_GROUPS[g])[d]
            rootattrs.append(f'this:Subassembly.{n}="[new EnumType({key}, &quot;{d}&quot;, &quot;{g}&quot;)]"')

    members = ['<x:Property Name="Geometry" Type="InOutArgument(asw:Geometry)" />',
               '<x:Property Name="SubassemblyErrorCenter" Type="InOutArgument(asw:SubassemblyErrorCenter)" />',
               '<x:Property Name="SubassemblyRunMode" Type="InOutArgument(asw:SubassemblyRunMode)" />']
    for n, disp, pv, on, tk in TARGETS:
        members.append(f'<x:Property Name="{n}" Type="InArgument(asw:{"OffsetTarget" if tk == "offset" else "ElevationTarget"})"><x:Property.Attributes>'
                       f'<asw:DisplayName2Attribute DisplayName="{q(disp)}" /><asw:EnabledFlag2Attribute EnabledFlag="True" />'
                       f'</x:Property.Attributes></x:Property>')
    members.append('<x:Property Name="Side" Type="InArgument(asw:EnumType)"><x:Property.Attributes><asw:EnabledFlag2Attribute EnabledFlag="True" /></x:Property.Attributes></x:Property>')
    for n, t, d, disp in PARAMS:
        ty = "asw:EnumType" if t.startswith("enum") else {"double": "x:Double", "grade": "asw:Grade", "string": "x:String"}[t]
        members.append(f'<x:Property Name="{n}" Type="InArgument({ty})"><x:Property.Attributes>'
                       f'<asw:EnabledFlag2Attribute EnabledFlag="True" /><asw:DisplayName2Attribute DisplayName="{q(disp)}" />'
                       f'</x:Property.Attributes></x:Property>')
    for n, disp in OUTPUTS:
        members.append(f'<x:Property Name="{n}" Type="OutArgument(x:Double)"><x:Property.Attributes>'
                       f'<asw:EnabledFlag2Attribute EnabledFlag="True" /><asw:DisplayName2Attribute DisplayName="{q(disp)}" />'
                       f'</x:Property.Attributes></x:Property>')

    variables = "".join(f'<Variable x:TypeArguments="x:{VARTYPES[n]}" Name="{n}" />' for n in EXPR)
    ENUMS = [("AwayFromCrown",41),("Left",1),("LeftInsideLane",20),("LeftInsideShoulder",21),("LeftOutsideLane",22),
             ("LeftOutsideShoulder",23),("No",11),("None",-1),("Right",0),("RightInsideLane",24),("RightInsideShoulder",25),
             ("RightOutsideLane",26),("RightOutsideShoulder",27),("Supported",30),("TowardsCrown",40),("Unsupported",31),("Yes",10)]
    for _g, _items in ENUM_GROUPS.items():
        ENUMS += [(i[0], i[2]) for i in _items]
    ENUMS.sort(key=lambda e: e[0].lower())
    variables += "".join(f'<Variable x:TypeArguments="asw:EnumType" Default="[new EnumType({v}, &quot;{n}&quot;)]" Modifiers="ReadOnly" Name="{n}" />' for n, v in ENUMS)

    xaml = f'''﻿<Activity mc:Ignorable="sads sap" x:Class="Subassembly" {" ".join(rootattrs)}
     xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
     xmlns:asa="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.API"
     xmlns:asa1="clr-namespace:Autodesk.SubassemblyComposer.ActivityLibrary;assembly=Subassembly.ActivityLibrary"
     xmlns:asa2="clr-namespace:Autodesk.SubassemblyComposer.API;assembly=Subassembly.WorkflowEngine"
     xmlns:asw="clr-namespace:Autodesk.SubassemblyComposer.WorkflowEngine;assembly=Subassembly.WorkflowEngine"
     xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
     xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
     xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities"
     xmlns:s="clr-namespace:System;assembly=mscorlib"
     xmlns:s1="clr-namespace:System;assembly=System.Core"
     xmlns:s2="clr-namespace:System;assembly=System"
     xmlns:s3="clr-namespace:System;assembly=System.Runtime.WindowsRuntime"
     xmlns:s4="clr-namespace:System;assembly=System.ServiceModel"
     xmlns:sa="clr-namespace:System.Activities;assembly=System.Activities"
     xmlns:sads="http://schemas.microsoft.com/netfx/2010/xaml/activities/debugger"
     xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
     xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib"
     xmlns:this="clr-namespace:"
     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <x:Members>{"".join(members)}</x:Members>
      <mva:VisualBasic.Settings>Assembly references and imported namespaces serialized as XML namespaces</mva:VisualBasic.Settings>
      <Flowchart sap:VirtualizedContainerService.HintSize="684,636" mva:VisualBasic.Settings="Assembly references and imported namespaces serialized as XML namespaces">
        <Flowchart.Variables>{variables}</Flowchart.Variables>
        <sap:WorkflowViewStateService.ViewState><scg:Dictionary x:TypeArguments="x:String, x:Object">
          <x:Boolean x:Key="IsExpanded">False</x:Boolean><av:Point x:Key="ShapeLocation">110,2.5</av:Point><av:Size x:Key="ShapeSize">60,75</av:Size>
          <av:PointCollection x:Key="ConnectorLocation">140,77.5 140,29</av:PointCollection><x:Double x:Key="Width">670</x:Double>
        </scg:Dictionary></sap:WorkflowViewStateService.ViewState>
        <Flowchart.StartNode>{chain}</Flowchart.StartNode>
        {refs}
      </Flowchart>
    </Activity>'''

    # ----------------------------------------------------------------- .atc
    def atcparam(n, t, d, disp):
        if t == "double": return f'<{n} DataType="double" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{d}</{n}>'
        if t == "string": return f'<{n} DataType="string" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{escape(d)}</{n}>'
        if t == "grade":  return f'<{n} DataType="double" TypeInfo="9" DisplayName="{q(disp)}" Description="{n}">{d}</{n}>'
        if t.startswith("enum:"):
            items = ENUM_GROUPS[t[5:]]; key = dict((i[0], i[2]) for i in items)[d]
            en = "".join(f'<{i[0]} DisplayName="{q(i[1])}">{i[2]}</{i[0]}>' for i in items)
            return f'<{n} DataType="long" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{key}<Enum>{en}</Enum></{n}>'
        return (f'<{n} DataType="long" TypeInfo="16" DisplayName="{q(disp)}" Description="{n}">{10 if d=="Yes" else 11}'
                f'<Enum><Yes DisplayName="Yes">10</Yes><No DisplayName="No">11</No></Enum></{n}>')
    atc = f'''<?xml version="1.0"?>
    <Category xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <ItemID idValue="{{{uuid.uuid4()}}}" />
      <Properties>
        <ItemName>Category1</ItemName>
        <Images>
          <Image cx="93" cy="123" />
        </Images>
      </Properties>
      <CustomData />
      <Source />
      <Palettes />
      <Packages />
      <Tools>
        <Tool Name="{NAME}">
          <ItemID idValue="{{{uuid.uuid4()}}}" />
          <Properties>
            <ItemName>{NAME}</ItemName>
            <Images>
              <Image cx="64" cy="64" />
            </Images>
            <ToolTip>Version: {VERSION}</ToolTip>
            <Help>
              <HelpFile />
              <HelpCommand />
              <HelpData />
            </Help>
          </Properties>
          <Source />
          <StockToolRef idValue="{{7F55AAC0-0256-48D7-BFA5-914702663FDE}}" />
          <Data>
            <AeccDbSubassembly>
              <GeometryGenerateMode>UseDotNet</GeometryGenerateMode>
              <DotNetClass Assembly="{GUID}.xaml">Subassembly.{NAME}</DotNetClass>
              <Version>{VERSION}</Version>
              <Params>
                <Side DataType="long" TypeInfo="16" DisplayName="Side" Description="Side">0<Enum><Left DisplayName="Left">1</Left><Right DisplayName="Right">0</Right></Enum></Side>
                {chr(10).join("            "+atcparam(*p) for p in PARAMS).strip()}
              </Params>
            </AeccDbSubassembly>
            <Units>m</Units>
          </Data>
        </Tool>
      </Tools>
      <StockTools />
    </Category>'''

    emd = f'''<?xml version="1.0"?>
    <EnumData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <EnumDatas>
        <Groups>
    {"".join(f'      <EnumGroup Name="{g}">' + chr(10) + '        <Items>' + chr(10) + "".join(f'          <EnumItem Name="{i[0]}" DisplayName="{q(i[1])}" Key="{i[2]}" />' + chr(10) for i in items) + '        </Items>' + chr(10) + '      </EnumGroup>' + chr(10) for g, items in ENUM_GROUPS.items())}    </Groups>
        <DefinedVariables>
    {chr(10).join(f"      <string>{n}</string>" for n in EXPR)}
        </DefinedVariables>
      </EnumDatas>
    </EnumData>'''
    cfg = '''<?xml version="1.0"?>
    <Configuration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <CreatedWith>
        <ProductName>Autodesk Subassembly Composer</ProductName>
        <Version>ForVail</Version>
        <VersionNumber>13.8.1516.0</VersionNumber>
      </CreatedWith>
    </Configuration>'''
    cdmd = '''<?xml version="1.0"?>
    <CodeData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <CodeItems />
    </CodeData>'''
    pvd = '''<?xml version="1.0"?>
    <PreviewData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <Superelevation>
        <CrossSlopes>
    ''' + "".join(f'      <PreviewCrossSlope CrossSegmentType="{c}" Slope="{s}" IsDefined="true" />\n'
                  for c, s in [("LeftInsideLane",-0.02),("LeftInsideShoulder",-0.05),("LeftOutsideLane",-0.02),("LeftOutsideShoulder",-0.05),
                               ("RightInsideLane",-0.02),("RightInsideShoulder",-0.05),("RightOutsideLane",-0.02),("RightOutsideShoulder",-0.05)]) + '''    </CrossSlopes>
      </Superelevation>
      <Cant>
        <CantParams>
          <PreviewCantParam Name="CantPivotType" Value="CenterLine" />
          <PreviewCantParam Name="LeftRail" Value="" />
          <PreviewCantParam Name="LeftRailDeltaElevation" Value="0" />
          <PreviewCantParam Name="RightRail" Value="" />
          <PreviewCantParam Name="RightRailDeltaElevation" Value="0" />
        </CantParams>
      </Cant>
    </PreviewData>'''
    ctypes = ('﻿<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
              '<Default Extension="atc" ContentType="" /><Default Extension="cfg" ContentType="" /><Default Extension="xaml" ContentType="" />'
              '<Default Extension="pvd" ContentType="" /><Default Extension="emd" ContentType="" /><Default Extension="cdmd" ContentType="" /></Types>')

    import xml.etree.ElementTree as ET
    ET.fromstring(xaml.encode("utf-8")); ET.fromstring(atc.encode()); ET.fromstring(emd.encode())
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(f"{GUID}.atc", atc.encode("utf-8"))
        z.writestr("[Content_Types].xml", ctypes.encode("utf-8"))
        z.writestr(f"{GUID}.cfg", cfg.encode("utf-8"))
        z.writestr(f"{GUID}.xaml", xaml.encode("utf-8"))
        z.writestr(f"{GUID}.pvd", pvd.encode("utf-8"))
        z.writestr(f"{GUID}.emd", emd.encode("utf-8"))
        z.writestr(f"{GUID}.cdmd", cdmd.encode("utf-8"))
    print("ok", OUT, "steps:", len(STEPS))
