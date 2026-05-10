using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AFA_ColumnCAD
{
    /// <summary>
    /// Classe auxiliar para operações com pilares estruturais no Revit.
    /// </summary>
    public static class ColumnParameterHelper
    {
        #region Validação e Geometria

        /// <summary>
        /// Verifica se um elemento do Revit é um pilar estrutural válido.
        /// </summary>
        /// <param name="element">O elemento a validar.</param>
        /// <returns>True se for um pilar estrutural válido, caso contrário False.</returns>
        public static bool IsValidStructuralColumn(Element element)
        {
            if (!(element is FamilyInstance column))
                return false;

            // Verificar se é um pilar estrutural e se pertence à categoria correta
            return column.StructuralType == StructuralType.Column &&
                   column.Category?.Id?.Value == (long)BuiltInCategory.OST_StructuralColumns;
        }

        /// <summary>
        /// Obtém informações detalhadas sobre a geometria do pilar (rectangular ou circular).
        /// </summary>
        /// <param name="column">A instância do pilar.</param>
        /// <returns>Objeto ColumnGeometryInfo contendo as dimensões e o tipo de geometria.</returns>
        public static ColumnGeometryInfo GetColumnGeometry(FamilyInstance column)
        {
            var geometryInfo = new ColumnGeometryInfo();

            try
            {
                // Tentar obter parâmetros de dimensões comuns (b, h, D ou nomes em inglês)
                var bParam = column.LookupParameter("b") ?? column.LookupParameter("Width") ?? column.LookupParameter("Largura");
                var hParam = column.LookupParameter("h") ?? column.LookupParameter("Height") ?? column.LookupParameter("Altura") ?? column.LookupParameter("Depth");
                var dParam = column.LookupParameter("D") ?? column.LookupParameter("Diameter") ?? column.LookupParameter("Diâmetro");

                if (dParam != null && dParam.HasValue)
                {
                    // Geometria Circular
                    geometryInfo.IsCircular = true;
                    geometryInfo.Diameter = dParam.AsDouble();
                }
                else if (bParam != null && bParam.HasValue)
                {
                    // Geometria Rectangular
                    geometryInfo.IsCircular = false;
                    geometryInfo.Width = bParam.AsDouble();

                    if (hParam != null && hParam.HasValue)
                        geometryInfo.Height = hParam.AsDouble();
                }
            }
            catch (Exception)
            {
                // Fallback: assumir como rectangular em caso de erro na leitura dos parâmetros
                geometryInfo.IsCircular = false;
            }

            return geometryInfo;
        }

        /// <summary>
        /// Valida se os valores dos parâmetros de armadura seguem o formato esperado.
        /// </summary>
        /// <param name="asVertical">Valor para a armadura vertical.</param>
        /// <param name="asEstribo">Valor para os estribos.</param>
        /// <returns>Resultado da validação com indicação de erro, se aplicável.</returns>
        public static ValidationResult ValidateReinforcementParameters(string asVertical, string asEstribo)
        {
            var result = new ValidationResult { IsValid = true };

            // Validar As_vertical (exemplo esperado: 4f10 ou similar)
            if (string.IsNullOrWhiteSpace(asVertical))
            {
                result.IsValid = false;
                result.ErrorMessage = "O parâmetro As_vertical não pode estar vazio.";
                return result;
            }

            // Validar As_estribo (exemplo esperado: f8//0.125 ou similar)
            if (string.IsNullOrWhiteSpace(asEstribo))
            {
                result.IsValid = false;
                result.ErrorMessage = "O parâmetro As_estribo não pode estar vazio.";
                return result;
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Contentor para informações geométricas de um pilar.
    /// </summary>
    public class ColumnGeometryInfo
    {
        /// <summary>Indica se o pilar é circular.</summary>
        public bool IsCircular { get; set; }
        /// <summary>Largura do pilar (se rectangular).</summary>
        public double Width { get; set; }
        /// <summary>Altura do pilar (se rectangular).</summary>
        public double Height { get; set; }
        /// <summary>Diâmetro do pilar (se circular).</summary>
        public double Diameter { get; set; }
    }

    /// <summary>
    /// Representa o resultado de uma operação de validação.
    /// </summary>
    public class ValidationResult
    {
        /// <summary>Indica se a validação teve sucesso.</summary>
        public bool IsValid { get; set; }
        /// <summary>Mensagem de erro detalhada em caso de falha.</summary>
        public string ErrorMessage { get; set; }
    }
}
