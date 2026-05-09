using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.ApplicationServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Classe principal para adicionar parâmetros de armadura a pilares estruturais
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class StructuralColumnParametersCommand : IExternalCommand
{
    private const string AS_VERTICAL_PARAM = "As_vertical";
    private const string AS_ESTRIBO_PARAM = "As_estribo";
    private const string ESTRIBO_ADICIONAL_PARAM = "Estribo_Adicional";
    private const string DEFAULT_AS_VERTICAL = "4f8";
    private const string DEFAULT_AS_ESTRIBO = "f6//0.125";
    private const string SHARED_PARAM_FILE = "StructuralColumnParams.txt";
    private const string SHARED_PARAM_GROUP_NAME = "Armadura";
    private const string SCHEDULE_NAME = "Quadro de Pilares";
    private static readonly ForgeTypeId REINFORCEMENT_GROUP_TYPE_ID = ResolveReinforcementGroupTypeId();

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiapp = commandData.Application;
        UIDocument uidoc = uiapp.ActiveUIDocument;
        Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
        Document doc = uidoc.Document;

        try
        {
            // Obter todos os pilares estruturais
            var allColumns = GetAllStructuralColumns(doc);

            if (!allColumns.Any())
            {
                TaskDialog.Show("Aviso", "Não foram encontrados pilares estruturais no projeto.");
                return Result.Succeeded;
            }

            // Verificar quais pilares não têm os parâmetros
            var columnsWithoutParameters = GetColumnsWithoutParameters(allColumns);

            // Se todos os pilares já têm os parâmetros
            if (!columnsWithoutParameters.Any())
            {
                TaskDialog.Show("Informação", "As entradas para armadura nos pilares já se encontram criadas.");
                // Garantir que a tabela existe
                EnsureScheduleExists(doc);
                return Result.Succeeded;
            }

            using (Autodesk.Revit.DB.Transaction trans = new Autodesk.Revit.DB.Transaction(doc, "Adicionar Parâmetros aos Pilares Estruturais"))
            {
                trans.Start();

                // Criar ficheiro de parâmetros partilhados se não existir
                string sharedParamFilePath = CreateSharedParameterFile(app);

                // Garantir que os parâmetros partilhados existem no documento
                EnsureSharedParametersExist(app, doc, sharedParamFilePath);

                // Definir valores predefinidos apenas para tipos de pilares de betão que não têm os parâmetros
                int typesProcessed = SetDefaultParameterValues(doc);

                // Criar ou atualizar o mapa de quantidades
                CreateOrUpdateColumnQuantitiesSchedule(doc);

                trans.Commit();

                TaskDialog.Show("Sucesso",
                    $"Parâmetros adicionados com sucesso a {typesProcessed} tipo(s) de pilar de betão!\n" +
                    $"Total de pilares no projeto: {allColumns.Count}\n\n" +
                    "O mapa de quantidades foi criado/atualizado e os novos pilares aparecerão automaticamente.");
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = $"Erro: {ex.Message}";
            return Result.Failed;
        }
    }

    // Obter todos os pilares estruturais
    private List<FamilyInstance> GetAllStructuralColumns(Autodesk.Revit.DB.Document doc)
    {
        FilteredElementCollector collector = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .WhereElementIsNotElementType();

        return collector.Cast<FamilyInstance>()
            .Where(column => column.StructuralType == StructuralType.Column)
            .ToList();
    }

    // Obter pilares sem parâmetros (apenas betão)
    private List<FamilyInstance> GetColumnsWithoutParameters(List<FamilyInstance> allColumns)
    {
        var columnsWithoutParams = new List<FamilyInstance>();
        var processedSymbols = new HashSet<ElementId>();
        foreach (var column in allColumns)
        {
            // Só criar parâmetros para betão
            if (!HasConcreteMaterial(column))
                continue;
            var symbol = column.Symbol;
            if (processedSymbols.Contains(symbol.Id))
                continue;
            Parameter asVerticalParam = symbol.LookupParameter(AS_VERTICAL_PARAM);
            Parameter asEstriboParam = symbol.LookupParameter(AS_ESTRIBO_PARAM);
            Parameter asEstriboAdicionalParam = symbol.LookupParameter(ESTRIBO_ADICIONAL_PARAM);
            if (asVerticalParam == null || asEstriboParam == null || asEstriboAdicionalParam == null)
            {
                columnsWithoutParams.Add(column);
            }
            processedSymbols.Add(symbol.Id);
        }
        return columnsWithoutParams;
    }

    // Garantir que os parâmetros partilhados existem
    private void EnsureSharedParametersExist(Autodesk.Revit.ApplicationServices.Application app, Document doc, string sharedParamFilePath)
    {
        try
        {
            app.SharedParametersFilename = sharedParamFilePath;
            DefinitionFile defFile = app.OpenSharedParameterFile();
            if (defFile == null)
                throw new Exception("Não foi possível abrir o ficheiro de parâmetros partilhados.");

            DefinitionGroup group = defFile.Groups.get_Item(SHARED_PARAM_GROUP_NAME);
            if (group == null)
            {
                group = defFile.Groups.Create(SHARED_PARAM_GROUP_NAME);
            }

            Definition asVerticalDef = group.Definitions.get_Item(AS_VERTICAL_PARAM);
            if (asVerticalDef == null)
            {
                ExternalDefinitionCreationOptions opt = new ExternalDefinitionCreationOptions(AS_VERTICAL_PARAM, SpecTypeId.String.Text);
                asVerticalDef = group.Definitions.Create(opt);
            }
            Definition asEstriboDef = group.Definitions.get_Item(AS_ESTRIBO_PARAM);
            if (asEstriboDef == null)
            {
                ExternalDefinitionCreationOptions opt = new ExternalDefinitionCreationOptions(AS_ESTRIBO_PARAM, SpecTypeId.String.Text);
                asEstriboDef = group.Definitions.Create(opt);
            }
            Definition asEstriboAdicionalDef = group.Definitions.get_Item(ESTRIBO_ADICIONAL_PARAM);
            if (asEstriboAdicionalDef == null)
            {
                ExternalDefinitionCreationOptions opt = new ExternalDefinitionCreationOptions(ESTRIBO_ADICIONAL_PARAM, SpecTypeId.String.Text);
                asEstriboAdicionalDef = group.Definitions.Create(opt);
            }

            CategorySet categorySet = app.Create.NewCategorySet();
            Category structuralColumnCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_StructuralColumns);
            categorySet.Insert(structuralColumnCategory);

            InstanceBinding binding = app.Create.NewInstanceBinding(categorySet);
            BindingMap bindingMap = doc.ParameterBindings;
            if (!bindingMap.Contains(asVerticalDef))
            {
                bindingMap.Insert(asVerticalDef, binding, REINFORCEMENT_GROUP_TYPE_ID);
            }
            if (!bindingMap.Contains(asEstriboDef))
            {
                bindingMap.Insert(asEstriboDef, binding, REINFORCEMENT_GROUP_TYPE_ID);
            }
            if (!bindingMap.Contains(asEstriboAdicionalDef))
            {
                bindingMap.Insert(asEstriboAdicionalDef, binding, REINFORCEMENT_GROUP_TYPE_ID);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Erro ao garantir que os parâmetros partilhados existem: {ex.Message}");
        }
    }

    // Criar ficheiro de parâmetros partilhados se não existir
    private string CreateSharedParameterFile(Autodesk.Revit.ApplicationServices.Application app)
    {
        string tempPath = Path.GetTempPath();
        string sharedParamFilePath = Path.Combine(tempPath, SHARED_PARAM_FILE);

        if (!File.Exists(sharedParamFilePath))
        {
            using (StreamWriter writer = new StreamWriter(sharedParamFilePath))
            {
                writer.WriteLine("# Este é um ficheiro de parâmetros partilhados do Revit.");
                writer.WriteLine("# Não editar manualmente.");
                writer.WriteLine("*META\tVERSION\tMINVERSION");
                writer.WriteLine("META\t2\t1");
                writer.WriteLine("*GROUP\tID\tNAME");
                writer.WriteLine($"GROUP\t1\t{SHARED_PARAM_GROUP_NAME}");
                writer.WriteLine("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE");
                writer.WriteLine($"PARAM\t{{{Guid.NewGuid()}}}\t{AS_VERTICAL_PARAM}\tTEXT\t\t1\t1\tArmadura vertical do pilar\t1");
                writer.WriteLine($"PARAM\t{{{Guid.NewGuid()}}}\t{AS_ESTRIBO_PARAM}\tTEXT\t\t1\t1\tArmadura transversal (estribos) do pilar\t1");
                writer.WriteLine($"PARAM\t{{{Guid.NewGuid()}}}\t{ESTRIBO_ADICIONAL_PARAM}\tTEXT\t\t1\t1\tEstribo adicional do pilar\t1");
            }
        }

        return sharedParamFilePath;
    }

    // Obter grupo de parâmetros para armaduras (com fallback para estrutural)
    private static ForgeTypeId ResolveReinforcementGroupTypeId()
    {
        var rebarProperty = typeof(GroupTypeId).GetProperty("Rebar");
        if (rebarProperty?.GetValue(null) is ForgeTypeId rebarGroup)
            return rebarGroup;

        var reinforcementProperty = typeof(GroupTypeId).GetProperty("Reinforcement");
        if (reinforcementProperty?.GetValue(null) is ForgeTypeId reinforcementGroup)
            return reinforcementGroup;

        return GroupTypeId.Structural;
    }

    // Definir valores predefinidos para os parâmetros (apenas betão, ao nível do tipo)
    private int SetDefaultParameterValues(Document doc)
    {
        try
        {
            // Obter todos os tipos de pilares estruturais de betão
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .Cast<FamilySymbol>()
                .Where(symbol =>
                {
                    // Verificar se o material do tipo é betão
                    var matParam = symbol.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                    if (matParam == null || matParam.StorageType != StorageType.ElementId)
                        return false;
                    var matId = matParam.AsElementId();
                    if (matId == ElementId.InvalidElementId)
                        return false;
                    var mat = doc.GetElement(matId) as Material;
                    return mat != null && mat.Name != null && mat.Name.ToLower().Contains("betão");
                })
                .ToList();

            int typesProcessed = 0;
            foreach (var symbol in symbols)
            {
                Parameter asVerticalParam = symbol.LookupParameter(AS_VERTICAL_PARAM);
                Parameter asEstriboParam = symbol.LookupParameter(AS_ESTRIBO_PARAM);
                bool wrote = false;
                if (asVerticalParam != null && !asVerticalParam.IsReadOnly && string.IsNullOrEmpty(asVerticalParam.AsString()))
                {
                    asVerticalParam.Set(DEFAULT_AS_VERTICAL);
                    wrote = true;
                }
                if (asEstriboParam != null && !asEstriboParam.IsReadOnly && string.IsNullOrEmpty(asEstriboParam.AsString()))
                {
                    asEstriboParam.Set(DEFAULT_AS_ESTRIBO);
                    wrote = true;
                }
                if (wrote) typesProcessed++;
            }
            if (typesProcessed == 0)
                TaskDialog.Show("Informação", "Não foi possível processar nenhum tipo de pilar de betão (parâmetros já existentes ou não aplicáveis).");
            return typesProcessed;
        }
        catch (Exception ex)
        {
            throw new Exception($"Erro ao definir valores predefinidos: {ex.Message}");
        }
    }

    // Comparador para FamilySymbol por Id
    private class SymbolIdComparer : IEqualityComparer<FamilySymbol>
    {
        public bool Equals(FamilySymbol x, FamilySymbol y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return x.Id == y.Id;
        }
        public int GetHashCode(FamilySymbol obj)
        {
            return obj.Id.GetHashCode();
        }
    }

    // Garantir que a tabela existe
    private void EnsureScheduleExists(Autodesk.Revit.DB.Document doc)
    {
        FilteredElementCollector scheduleCollector = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule));

        bool scheduleExists = scheduleCollector.Cast<ViewSchedule>()
            .Any(schedule => schedule.Name == SCHEDULE_NAME);

        if (!scheduleExists)
        {
            CreateOrUpdateColumnQuantitiesSchedule(doc);
        }
    }

    // Criar ou atualizar o mapa de quantidades
    private void CreateOrUpdateColumnQuantitiesSchedule(Autodesk.Revit.DB.Document doc)
    {
        try
        {
            FilteredElementCollector scheduleCollector = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule));

            foreach (ViewSchedule existingSchedule in scheduleCollector.Cast<ViewSchedule>())
            {
                if (existingSchedule.Name == SCHEDULE_NAME)
                {
                    doc.Delete(existingSchedule.Id);
                    break;
                }
            }

            ElementId categoryId = new ElementId(BuiltInCategory.OST_StructuralColumns);
            ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, categoryId);
            schedule.Name = SCHEDULE_NAME;

            ScheduleDefinition definition = schedule.Definition;

            AddScheduleFields(definition, doc);
            AddScheduleFilters(definition);

            if (definition.GetFieldCount() > 0)
            {
                ScheduleSortGroupField sortField = new ScheduleSortGroupField(definition.GetFieldId(0));
                sortField.ShowHeader = false;
                definition.AddSortGroupField(sortField);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Erro ao criar/atualizar mapa de quantidades: {ex.Message}");
        }
    }

    // Adicionar campos à tabela: sempre mostrar As_vertical e As_estribo
    private void AddScheduleFields(ScheduleDefinition definition, Document doc)
    {
        try
        {
            IList<SchedulableField> schedulableFields = definition.GetSchedulableFields();

            // 1. Designação (Mark)
            var markField = schedulableFields.FirstOrDefault(f =>
                f.GetName(doc) == "Mark" ||
                f.GetName(doc) == "Marca" ||
                f.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_MARK));
            if (markField != null)
            {
                ScheduleField field = definition.AddField(markField);
                field.ColumnHeading = "Designacao";
            }

            // 2. Tipo (Type Name)
            var typeNameField = schedulableFields.FirstOrDefault(f =>
                f.GetName(doc) == "Type Name" ||
                f.GetName(doc) == "Tipo" ||
                f.ParameterId == new ElementId(BuiltInParameter.SYMBOL_NAME_PARAM));
            if (typeNameField != null)
            {
                ScheduleField field = definition.AddField(typeNameField);
                field.ColumnHeading = "Tipo";
            }

            // 3. As_vertical (aparece sempre na tabela)
            var asVerticalField = schedulableFields.FirstOrDefault(f =>
                f.GetName(doc) == AS_VERTICAL_PARAM);
            if (asVerticalField != null)
            {
                ScheduleField field = definition.AddField(asVerticalField);
                field.ColumnHeading = "As_vertical";
            }

            // 4. As_estribo (aparece sempre na tabela)
            var asEstriboField = schedulableFields.FirstOrDefault(f =>
                f.GetName(doc) == AS_ESTRIBO_PARAM);
            if (asEstriboField != null)
            {
                ScheduleField field = definition.AddField(asEstriboField);
                field.ColumnHeading = "As_estribo";
            }

            // 5. Estribo_Adicional (aparece sempre na tabela)
            var asEstriboAdicionalField = schedulableFields.FirstOrDefault(f =>
                f.GetName(doc) == ESTRIBO_ADICIONAL_PARAM);
            if (asEstriboAdicionalField != null)
            {
                ScheduleField field = definition.AddField(asEstriboAdicionalField);
                field.ColumnHeading = "Estribo_Adicional";
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Erro ao adicionar campos ao mapa: {ex.Message}");
        }
    }

    // Filtros da tabela (mantido para compatibilidade)
    private void AddScheduleFilters(ScheduleDefinition definition)
    {
        try
        {
            // Não adicionar filtros específicos
        }
        catch (Exception)
        {
            // Se não conseguir adicionar filtro, continuar sem ele
        }
    }

    // Verificar se o material é betão
    private bool HasConcreteMaterial(FamilyInstance column)
    {
        try
        {
            var materialIds = column.GetMaterialIds(false);
            foreach (ElementId materialId in materialIds)
            {
                Material material = column.Document.GetElement(materialId) as Material;
                if (material != null && material.Name != null && material.Name.ToLower().Contains("betão"))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Ignorar erros na verificação do material
        }
        return false;
    }
}
