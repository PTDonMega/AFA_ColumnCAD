using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.ApplicationServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AFA_ColumnCAD
{
    /// <summary>
    /// Classe principal para adicionar parâmetros de armadura a pilares estruturais no Revit.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class StructuralColumnParametersCommand : IExternalCommand
    {
        #region Constantes e Campos Privados

        private const string AS_VERTICAL_PARAM = "As_vertical";
        private const string AS_ESTRIBO_PARAM = "As_estribo";
        private const string ESTRIBO_ADICIONAL_PARAM = "Estribo_Adicional";
        private const string DEFAULT_AS_VERTICAL = "4f8";
        private const string DEFAULT_AS_ESTRIBO = "f6//0.125";
        private const string SHARED_PARAM_FILE = "StructuralColumnParams.txt";
        private const string SHARED_PARAM_GROUP_NAME = "Armadura";
        private const string SCHEDULE_NAME = "Quadro de Pilares";
        private static readonly string[] REINFORCEMENT_GROUP_LABEL_KEYWORDS = { "armadura", "reinforcement", "rebar" };
        private static readonly ForgeTypeId REINFORCEMENT_GROUP_TYPE_ID = ResolveReinforcementGroupTypeId();

        #endregion

        #region Execução do Comando (Execute)

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

                // Verificar se existem parâmetros de projeto (bind global)
                bool hasProjectBindings = CheckIfProjectBindingsExist(doc);

                // Verificar quais pilares não têm os parâmetros
                var columnsWithoutParameters = GetColumnsWithoutParameters(allColumns);

                // Se todos os pilares já têm os parâmetros e não há projeto bindings para limpar
                if (!columnsWithoutParameters.Any() && !hasProjectBindings)
                {
                    TaskDialog.Show("Informação", "As entradas para armadura nos pilares já se encontram criadas.");
                    // Garantir que a tabela existe
                    EnsureScheduleExists(doc);
                    return Result.Succeeded;
                }

                // Criar ficheiro de parâmetros partilhados se não existir
                string sharedParamFilePath = CreateSharedParameterFile(app);

                using (Autodesk.Revit.DB.Transaction trans = new Autodesk.Revit.DB.Transaction(doc, "Preparar Parâmetros Partilhados"))
                {
                    trans.Start();
                    // Garante que o ficheiro TXT tem os parâmetros e remove ligações globais
                    EnsureSharedParametersExist(app, doc, sharedParamFilePath);
                    trans.Commit();
                }

                // Injetar os parâmetros diretamente nas famílias (fora de transação do documento)
                AddParametersToConcreteFamilies(app, doc);

                using (Autodesk.Revit.DB.Transaction trans = new Autodesk.Revit.DB.Transaction(doc, "Preencher Valores e Tabela"))
                {
                    trans.Start();

                    // Definir valores predefinidos apenas para tipos de pilares de betão
                    int typesProcessed = SetDefaultParameterValues(doc);

                    // Criar ou atualizar o mapa de quantidades
                    CreateOrUpdateColumnQuantitiesSchedule(doc);

                    trans.Commit();

                    TaskDialog.Show("Sucesso",
                        $"Parâmetros adicionados e definidos com sucesso!\n" +
                        $"Pilares de betão processados: {typesProcessed}\n\n" +
                        "O mapa de quantidades foi atualizado.");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Erro: {ex.Message}";
                return Result.Failed;
            }
        }

        #endregion

        #region Gestão de Parâmetros

        /// <summary>
        /// Obtém todos os pilares estruturais no documento atual.
        /// </summary>
        private List<FamilyInstance> GetAllStructuralColumns(Autodesk.Revit.DB.Document doc)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .WhereElementIsNotElementType();

            return collector.Cast<FamilyInstance>()
                .Where(column => column.StructuralType == StructuralType.Column)
                .ToList();
        }

        /// <summary>
        /// Filtra a lista de pilares para encontrar aqueles que ainda não possuem os parâmetros de armadura.
        /// </summary>
        private List<FamilyInstance> GetColumnsWithoutParameters(List<FamilyInstance> allColumns)
        {
            var columnsWithoutParams = new List<FamilyInstance>();
            foreach (var column in allColumns)
            {
                // Só criar parâmetros para betão
                if (!HasConcreteMaterial(column))
                    continue;

                Parameter asVerticalParam = column.LookupParameter(AS_VERTICAL_PARAM);
                Parameter asEstriboParam = column.LookupParameter(AS_ESTRIBO_PARAM);
                Parameter asEstriboAdicionalParam = column.LookupParameter(ESTRIBO_ADICIONAL_PARAM);
                
                if (asVerticalParam == null || asEstriboParam == null || asEstriboAdicionalParam == null)
                {
                    columnsWithoutParams.Add(column);
                }
            }
            return columnsWithoutParams;
        }

        /// <summary>
        /// Garante que os parâmetros partilhados estão corretamente configurados e vinculados à categoria de pilares.
        /// </summary>
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

                // Remover ligações globais antigas para limpar pilares de aço e madeira
                BindingMap bindingMap = doc.ParameterBindings;
                DefinitionBindingMapIterator it = bindingMap.ForwardIterator();
                it.Reset();
                List<Definition> existingDefsToRebind = new List<Definition>();
                while (it.MoveNext())
                {
                    Definition def = it.Key;
                    if (def.Name == AS_VERTICAL_PARAM || def.Name == AS_ESTRIBO_PARAM || def.Name == ESTRIBO_ADICIONAL_PARAM)
                    {
                        existingDefsToRebind.Add(def);
                    }
                }

                foreach (var def in existingDefsToRebind)
                {
                    bindingMap.Remove(def);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Erro ao garantir que os parâmetros partilhados existem: {ex.Message}");
            }
        }

        /// <summary>
        /// Verifica se ainda existem parâmetros associados a todo o projeto em vez de apenas às famílias.
        /// </summary>
        private bool CheckIfProjectBindingsExist(Document doc)
        {
            BindingMap bindingMap = doc.ParameterBindings;
            DefinitionBindingMapIterator it = bindingMap.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                Definition def = it.Key;
                if (def.Name == AS_VERTICAL_PARAM || def.Name == AS_ESTRIBO_PARAM || def.Name == ESTRIBO_ADICIONAL_PARAM)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Injeta os parâmetros de armadura diretamente nas famílias de betão.
        /// </summary>
        private void AddParametersToConcreteFamilies(Autodesk.Revit.ApplicationServices.Application app, Document doc)
        {
            var concreteColumns = GetAllStructuralColumns(doc).Where(HasConcreteMaterial).ToList();
            var distinctFamilies = concreteColumns.Select(c => c.Symbol.Family)
                                                  .GroupBy(f => f.Id).Select(g => g.First()).ToList();

            DefinitionFile defFile = app.OpenSharedParameterFile();
            if (defFile == null) return;
            DefinitionGroup group = defFile.Groups.get_Item(SHARED_PARAM_GROUP_NAME);
            if (group == null) return;

            Definition asVerticalDef = group.Definitions.get_Item(AS_VERTICAL_PARAM);
            Definition asEstriboDef = group.Definitions.get_Item(AS_ESTRIBO_PARAM);
            Definition asEstriboAdicionalDef = group.Definitions.get_Item(ESTRIBO_ADICIONAL_PARAM);

            foreach (Family family in distinctFamilies)
            {
                if (!family.IsEditable) continue;

                Document famDoc = doc.EditFamily(family);
                if (famDoc != null)
                {
                    bool needsLoad = false;
                    using (Transaction t = new Transaction(famDoc, "Adicionar Parâmetros na Família"))
                    {
                        t.Start();
                        FamilyManager fm = famDoc.FamilyManager;
                        
                        if (fm.get_Parameter(AS_VERTICAL_PARAM) == null && asVerticalDef != null)
                        {
                            fm.AddParameter((ExternalDefinition)asVerticalDef, REINFORCEMENT_GROUP_TYPE_ID, true);
                            needsLoad = true;
                        }
                        if (fm.get_Parameter(AS_ESTRIBO_PARAM) == null && asEstriboDef != null)
                        {
                            fm.AddParameter((ExternalDefinition)asEstriboDef, REINFORCEMENT_GROUP_TYPE_ID, true);
                            needsLoad = true;
                        }
                        if (fm.get_Parameter(ESTRIBO_ADICIONAL_PARAM) == null && asEstriboAdicionalDef != null)
                        {
                            fm.AddParameter((ExternalDefinition)asEstriboAdicionalDef, REINFORCEMENT_GROUP_TYPE_ID, true);
                            needsLoad = true;
                        }
                        t.Commit();
                    }

                    if (needsLoad)
                    {
                        famDoc.LoadFamily(doc, new ColumnFamilyLoadOptions());
                    }
                    famDoc.Close(false);
                }
            }
        }

        /// <summary>
        /// Cria o ficheiro de parâmetros partilhados temporário se este ainda não existir.
        /// </summary>
        private string CreateSharedParameterFile(Autodesk.Revit.ApplicationServices.Application app)
        {
            string tempPath = Path.GetTempPath();
            string sharedParamFilePath = Path.Combine(tempPath, SHARED_PARAM_FILE);

            if (File.Exists(sharedParamFilePath))
            {
                try
                {
                    // Corrigir ficheiros antigos que tenham GUIDs com chavetas (causa o erro readParamDatabase)
                    string content = File.ReadAllText(sharedParamFilePath);
                    if (content.Contains("{") || content.Contains("}"))
                    {
                        content = content.Replace("{", "").Replace("}", "");
                        File.WriteAllText(sharedParamFilePath, content);
                    }
                }
                catch
                {
                    // Ignorar erros de leitura/escrita se o ficheiro estiver bloqueado
                }
            }
            else
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
                    writer.WriteLine($"PARAM\t{Guid.NewGuid()}\t{AS_VERTICAL_PARAM}\tTEXT\t\t1\t1\tArmadura vertical do pilar\t1");
                    writer.WriteLine($"PARAM\t{Guid.NewGuid()}\t{AS_ESTRIBO_PARAM}\tTEXT\t\t1\t1\tArmadura transversal (estribos) do pilar\t1");
                    writer.WriteLine($"PARAM\t{Guid.NewGuid()}\t{ESTRIBO_ADICIONAL_PARAM}\tTEXT\t\t1\t1\tEstribo adicional do pilar\t1");
                }
            }

            return sharedParamFilePath;
        }

        /// <summary>
        /// Resolve o ID do grupo de parâmetros para armaduras, com fallback para o grupo estrutural.
        /// </summary>
        private static ForgeTypeId ResolveReinforcementGroupTypeId()
        {
            var rebarSetProperty = typeof(GroupTypeId).GetProperty("RebarSet");
            if (rebarSetProperty?.GetValue(null) is ForgeTypeId rebarSetGroup)
                return rebarSetGroup;

            var rebarProperty = typeof(GroupTypeId).GetProperty("Rebar");
            if (rebarProperty?.GetValue(null) is ForgeTypeId rebarGroup)
                return rebarGroup;

            var reinforcementProperty = typeof(GroupTypeId).GetProperty("Reinforcement");
            if (reinforcementProperty?.GetValue(null) is ForgeTypeId reinforcementGroup)
                return reinforcementGroup;

            var allGroupsMethod = typeof(ParameterUtils).GetMethod("GetAllBuiltInGroups", BindingFlags.Public | BindingFlags.Static);
            if (allGroupsMethod?.Invoke(null, null) is IEnumerable<ForgeTypeId> groups)
            {
                foreach (var group in groups)
                {
                    string label = LabelUtils.GetLabelForGroup(group)?.ToLowerInvariant() ?? string.Empty;
                    if (REINFORCEMENT_GROUP_LABEL_KEYWORDS.Any(keyword => label.Contains(keyword)))
                        return group;
                }
            }

            return GroupTypeId.Structural;
        }

        /// <summary>
        /// Define os valores predefinidos para os parâmetros nos pilares de betão encontrados.
        /// </summary>
        private int SetDefaultParameterValues(Document doc)
        {
            try
            {
                // Obter todos os pilares estruturais de betão
                var columns = GetAllStructuralColumns(doc).Where(HasConcreteMaterial).ToList();

                int instancesProcessed = 0;
                foreach (var column in columns)
                {
                    IList<Parameter> asVerticalParams = column.GetParameters(AS_VERTICAL_PARAM);
                    IList<Parameter> asEstriboParams = column.GetParameters(AS_ESTRIBO_PARAM);
                    bool wrote = false;
                    
                    foreach (var p in asVerticalParams)
                    {
                        if (p != null && !p.IsReadOnly && string.IsNullOrEmpty(p.AsString()))
                        {
                            p.Set(DEFAULT_AS_VERTICAL);
                            wrote = true;
                        }
                    }
                    foreach (var p in asEstriboParams)
                    {
                        if (p != null && !p.IsReadOnly && string.IsNullOrEmpty(p.AsString()))
                        {
                            p.Set(DEFAULT_AS_ESTRIBO);
                            wrote = true;
                        }
                    }
                    if (wrote) instancesProcessed++;
                }
                if (instancesProcessed == 0)
                    TaskDialog.Show("Informação", "Não foi possível processar nenhum pilar de betão (parâmetros já existentes ou não aplicáveis).");
                return instancesProcessed;
            }
            catch (Exception ex)
            {
                throw new Exception($"Erro ao definir valores predefinidos: {ex.Message}");
            }
        }

        #endregion

        #region Gestão de Mapas (Schedules)

        /// <summary>
        /// Garante que a tabela de quantidades de pilares existe no documento.
        /// </summary>
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

        /// <summary>
        /// Cria ou atualiza a tabela de quantidades de pilares no Revit.
        /// </summary>
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

        /// <summary>
        /// Adiciona os campos necessários à tabela de pilares.
        /// </summary>
        private void AddScheduleFields(ScheduleDefinition definition, Document doc)
        {
            try
            {
                IList<SchedulableField> schedulableFields = definition.GetSchedulableFields();

                // 1. Designação
                var markField = schedulableFields.FirstOrDefault(f =>
                    f.GetName(doc) == "Designacao");
                if (markField = null)
                {
                    throw new Exception("Não existem elementos com parametro 'Designacao' para filtrar na Schedule.");
                }
                else
                {
                    ScheduleField field = definition.AddField(markField);
                    field.ColumnHeading = "Designacao";
                }

                // 2. Tipo (Type Name)
                var typeNameField = schedulableFields.FirstOrDefault(f =>
                    f.GetName(doc) == "Type" ||
                    f.GetName(doc) == "Tipo" ||
                    f.ParameterId == new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM) ||
                    f.ParameterId == new ElementId(BuiltInParameter.SYMBOL_NAME_PARAM));
                if (typeNameField != null)
                {
                    ScheduleField field = definition.AddField(typeNameField);
                    field.ColumnHeading = "Family Type";
                }

                // 2.5. Nivel Superior (Top Level)
                var topLevelField = schedulableFields.FirstOrDefault(f =>
                    f.ParameterId == new ElementId(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM) ||
                    f.GetName(doc) == "Top Level");
                if (topLevelField != null)
                {
                    ScheduleField field = definition.AddField(topLevelField);
                    field.ColumnHeading = "Nível Superior";
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

        /// <summary>
        /// Adiciona filtros à tabela (reservado para uso futuro).
        /// </summary>
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

        #endregion

        #region Métodos Auxiliares

        /// <summary>
        /// Verifica se o pilar possui algum material de betão.
        /// </summary>
        private bool HasConcreteMaterial(FamilyInstance column)
        {
            try
            {
                // 1. Verifica pelo nome da família ou do tipo
                string familyName = column.Symbol.Family.Name.ToLower();
                string typeName = column.Symbol.Name.ToLower();
                if (familyName.Contains("betão") || familyName.Contains("betao") || familyName.Contains("concrete") ||
                    typeName.Contains("betão") || typeName.Contains("betao") || typeName.Contains("concrete"))
                {
                    return true;
                }

                // 2. Verifica pelo parâmetro Structural Material
                ElementId structMatId = column.StructuralMaterialId;
                if (structMatId != null && structMatId != ElementId.InvalidElementId)
                {
                    Material structMat = column.Document.GetElement(structMatId) as Material;
                    if (structMat != null && structMat.Name != null)
                    {
                        string matName = structMat.Name.ToLower();
                        if (matName.Contains("betão") || matName.Contains("betao") || matName.Contains("concrete"))
                        {
                            return true;
                        }
                    }
                }

                // 3. Verifica pelos materiais atribuídos à geometria
                var materialIds = column.GetMaterialIds(false);
                foreach (ElementId materialId in materialIds)
                {
                    Material material = column.Document.GetElement(materialId) as Material;
                    if (material != null && material.Name != null)
                    {
                        string matName = material.Name.ToLower();
                        if (matName.Contains("betão") || matName.Contains("betao") || matName.Contains("concrete"))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignorar erros na verificação do material
            }
            return false;
        }

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

        /// <summary>
        /// Classe auxiliar para opções de carregamento de família.
        /// </summary>
        private class ColumnFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        #endregion
    }
}
