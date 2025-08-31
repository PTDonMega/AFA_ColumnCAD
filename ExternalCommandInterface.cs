using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

/// <summary>
/// Comando externo para adicionar parâmetros de armadura aos pilares estruturais
/// Verifica todos os pilares individualmente e adiciona parâmetros apenas aos que não os possuem
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class AddColumnReinforcementParameters : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            // Criar instância do comando principal
            StructuralColumnParametersCommand command = new StructuralColumnParametersCommand();

            // Executar o comando
            return command.Execute(commandData, ref message, elements);
        }
        catch (System.Exception ex)
        {
            message = $"Erro inesperado: {ex.Message}";
            return Result.Failed;
        }
    }
}
