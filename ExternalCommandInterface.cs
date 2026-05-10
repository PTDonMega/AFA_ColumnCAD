using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace AFA_ColumnCAD
{
    /// <summary>
    /// Comando externo para adicionar parâmetros de armadura aos pilares estruturais.
    /// Esta classe serve como ponto de entrada alternativo que delega a lógica para StructuralColumnParametersCommand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AddColumnReinforcementParameters : IExternalCommand
    {
        /// <summary>
        /// Executa o comando de adição de parâmetros de armadura.
        /// </summary>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Criar instância do comando principal e executar
                StructuralColumnParametersCommand command = new StructuralColumnParametersCommand();
                return command.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                message = $"Erro inesperado no comando AddColumnReinforcementParameters: {ex.Message}";
                return Result.Failed;
            }
        }
    }
}
