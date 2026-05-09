using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Classe auxiliar para operações com pilares estruturais
/// </summary>
public static class ColumnParameterHelper
{
    /// <summary>
    /// Verifica se um elemento é um pilar estrutural válido
    /// </summary>
    public static bool IsValidStructuralColumn(Element element)
    {
        if (!(element is FamilyInstance column))
            return false;

        // Verificar se é um pilar estrutural
        return column.StructuralType == StructuralType.Column &&
               column.Category?.Id?.Value == (long)BuiltInCategory.OST_StructuralColumns;
    }

    /// <summary>
    /// Obtém informações sobre a geometria do pilar (rectangular ou circular)
    /// </summary>
    public static ColumnGeometryInfo GetColumnGeometry(FamilyInstance column)
    {
        var geometryInfo = new ColumnGeometryInfo();

        try
        {
            // Tentar obter parâmetros de dimensões
            var bParam = column.LookupParameter("b") ?? column.LookupParameter("Width") ?? column.LookupParameter("Largura");
            var hParam = column.LookupParameter("h") ?? column.LookupParameter("Height") ?? column.LookupParameter("Altura") ?? column.LookupParameter("Depth");
            var dParam = column.LookupParameter("D") ?? column.LookupParameter("Diameter") ?? column.LookupParameter("Diâmetro");

            if (dParam != null && dParam.HasValue)
            {
                // Pilar circular
                geometryInfo.IsCircular = true;
                geometryInfo.Diameter = dParam.AsDouble();
            }
            else if (bParam != null && bParam.HasValue)
            {
                // Pilar rectangular
                geometryInfo.IsCircular = false;
                geometryInfo.Width = bParam.AsDouble();

                if (hParam != null && hParam.HasValue)
                    geometryInfo.Height = hParam.AsDouble();
            }
        }
        catch (Exception)
        {
            // Se ocorrer erro, assumir como rectangular
            geometryInfo.IsCircular = false;
        }

        return geometryInfo;
    }

    /// <summary>
    /// Valida os valores dos parâmetros de armadura
    /// </summary>
    public static ValidationResult ValidateReinforcementParameters(string asVertical, string asEstribo)
    {
        var result = new ValidationResult { IsValid = true };

        // Validar As_vertical (formato: nfØ, ex: 4f10)
        if (string.IsNullOrWhiteSpace(asVertical))
        {
            result.IsValid = false;
            result.ErrorMessage = "O parâmetro As_vertical não pode estar vazio.";
            return result;
        }

        // Validar As_estribo (formato: fØ//c1/c2, ex: f8//0/125)
        if (string.IsNullOrWhiteSpace(asEstribo))
        {
            result.IsValid = false;
            result.ErrorMessage = "O parâmetro As_estribo não pode estar vazio.";
            return result;
        }

        return result;
    }
}

/// <summary>
/// Informações sobre a geometria do pilar
/// </summary>
public class ColumnGeometryInfo
{
    public bool IsCircular { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Diameter { get; set; }
}

/// <summary>
/// Resultado da validação de parâmetros
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; }
}
