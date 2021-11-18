﻿using System.Data;
using System.Reflection;
using System.Text.Json;

using CodeBits;

using Datask.Providers.Schemas;

using DotLiquid;

using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Datask.Tool.ExcelData.Core;

public sealed class DataExtensionBuilder
{
#pragma warning disable S3264 // Events should be invoked
    public event EventHandler<StatusEventArgs<StatusEvents>> OnStatus = null!;
#pragma warning restore S3264 // Events should be invoked

    private readonly DataHelperConfiguration _configuration;

    public DataExtensionBuilder(DataHelperConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task BuildDataExtensionAsync()
    {
        if (_configuration.Flavors is null)
            return;

        string filePath = _configuration.FilePath;
        RegisterTypes();
        File.WriteAllText(filePath, await RenderTemplate("PopulateDataTemplate", _configuration).ConfigureAwait(false));

        foreach (Flavors? flavour in _configuration.Flavors)
        {
            OnStatus.Fire(StatusEvents.Generate,
                new { Flavor = flavour.Name },
                "Generating data helper for {Flavor} information.");

            File.AppendAllText(filePath, await RenderTemplate("PopulateFlavorDataTemplate", flavour.Name).ConfigureAwait(false));
            using FileStream fs = new(flavour.ExcelPath, FileMode.Open, FileAccess.Read);
            IWorkbook xssWorkbook = new XSSFWorkbook(fs);

            int noOfWorkSheets = xssWorkbook.NumberOfSheets;
            await PopulateConsolidatedData(filePath, xssWorkbook, noOfWorkSheets).ConfigureAwait(false);

            for (int index = 0; index < noOfWorkSheets; index++)
            {
                var sheet = (XSSFSheet)xssWorkbook.GetSheetAt(index);

                List<XSSFTable> xssfTables = sheet.GetTables();
                if (!xssfTables.Any())
                    continue;

                string[] tableName = xssfTables.First().DisplayName.Split('.');
                TableBindingModel td = new(tableName.Skip(1).First(), tableName.Take(1).First());

                FillTableData(sheet, td, out int cellCount);

                IList<List<string?>> dataRows = FillDataRows(sheet, td, cellCount);

                //Remove autogenerated columns like timestamp
                foreach (ColumnBindingModel? col in td.Columns.Where(c => c.IsAutoGenerated).ToList())
                {
                    td.Columns.Remove(col);
                }

                flavour.TableDefinitions.Add(td);

                File.AppendAllText(filePath, await RenderTemplate("PopulateTableDataTemplate", new
                {
                    table = td,
                    dr = dataRows,
                    ic = td.Columns.Any(c => c.IsIdentity),
                }).ConfigureAwait(false));
            }

            File.AppendAllText(filePath, "}");
        }

        File.AppendAllText(filePath, "}");
    }

    private async Task PopulateConsolidatedData(string filePath, IWorkbook xssWorkbook, int noOfWorkSheets)
    {
        IList<TableBindingModel> tables = new List<TableBindingModel>();
        for (int index = 0; index < noOfWorkSheets; index++)
        {
            var sheet = (XSSFSheet)xssWorkbook.GetSheetAt(index);

            List<XSSFTable> xssfTables = sheet.GetTables();
            if (!xssfTables.Any())
                continue;

            string[] tableName = xssfTables.First().DisplayName.Split('.');
            tables.Add(new TableBindingModel(tableName.Skip(1).First(), tableName.Take(1).First()));
        }

        File.AppendAllText(filePath, await RenderTemplate("PopulateConsolidatedDataTemplate", tables
           .Select(t => $"{t.Schema}{t.Name}")
           .ToList()).ConfigureAwait(false));
    }


    private static void FillTableData(XSSFSheet sheet, TableBindingModel td, out int cellCount)
    {
        IRow headerRow = sheet.GetRow(0);
        //timestampCols = new();
        cellCount = headerRow.LastCellNum;
        for (int j = 0; j < cellCount; j++)
        {
            ICell cell = headerRow.GetCell(j);
            if (cell == null || string.IsNullOrWhiteSpace(cell.ToString()))
                continue;

            string? cellComment = cell.CellComment.String.ToString();
            if (cellComment is null)
                continue;

            Dictionary<string, object>? columnMetaData = JsonSerializer.Deserialize<Dictionary<string, object>>(cellComment, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            if (columnMetaData is null)
                continue;

            td.Columns.Add(new ColumnBindingModel(cell.ToString()!)
            {
                DbType = columnMetaData.TryGetValue("DbType", out object dbType) ? (DbType)Enum.Parse(typeof(DbType), dbType.ToString()) : default,
                DatabaseType = columnMetaData.TryGetValue("DbType", out object dType) ? $"DbType.{dType}" : default!,
                CSharpType = columnMetaData.TryGetValue("Type", out object type) ? type.ToString() : default!,
                IsPrimaryKey = columnMetaData.TryGetValue("IsPrimaryKey", out object isPrimaryKey) ? Convert.ToBoolean(isPrimaryKey.ToString()) : default,
                IsNullable = columnMetaData.TryGetValue("IsNullable", out object isNullable) ? Convert.ToBoolean(isNullable.ToString()) : default,
                IsIdentity = columnMetaData.TryGetValue("IsIdentity", out object isIdentity) ? Convert.ToBoolean(isIdentity.ToString()) : default,
                MaxLength = columnMetaData.TryGetValue("MaxLength", out object maxLength) ? Convert.ToInt32(maxLength.ToString()) : default,
                IsAutoGenerated = columnMetaData.TryGetValue("IsAutoGenerated", out object isAutoGenerated) ? Convert.ToBoolean(isAutoGenerated.ToString()) : default,
            });
        }
    }

    private static IList<List<string?>> FillDataRows(XSSFSheet sheet, TableBindingModel td, int cellCount)
    {
        IList<List<string?>> dataRows = new List<List<string?>>();

        for (int i = sheet.FirstRowNum + 1; i <= sheet.LastRowNum; i++)
        {
            List<string?> rowList = new();
            IRow row = sheet.GetRow(i);
            if (row == null)
                continue;
            if (row.Cells.All(d => d.CellType == CellType.Blank))
                continue;

            for (int j = row.FirstCellNum; j < cellCount; j++)
            {
                //Skip timestamp column data
                if (td.Columns[j].IsAutoGenerated)
                    continue;

                rowList.Add(row.GetCell(j) == null ? "string.Empty" :
                    ConvertObjectValToCSharpType(row.GetCell(j), td.Columns[j].DbType));
            }

            if (rowList.Count > 0)
                dataRows.Add(rowList);
        }

        return dataRows;
    }

    private static string ConvertObjectValToCSharpType(object rowValue, DbType colType)
    {
        return colType switch
        {
            DbType.Binary => $"BitConverter.GetBytes(Convert.ToUInt64({rowValue}))",
            DbType.Boolean => rowValue.ToString() == "0" ? "false" : "true",
            DbType.AnsiStringFixedLength => rowValue.ToString(),
            DbType.StringFixedLength => rowValue.ToString(),
            DbType.String => rowValue.ToString(),
            DbType.AnsiString => rowValue.ToString(),
            DbType.Xml => rowValue.ToString(),
            DbType.DateTime => $"DateTime.Parse(\"{rowValue}\")",
            DbType.Date => $"DateTime.Parse(\"{rowValue}\")",
            DbType.Time => $"DateTime.Parse(\"{rowValue}\")",
            DbType.DateTime2 => $"DateTime.Parse(\"{rowValue}\")",
            DbType.Decimal => $"{rowValue}",
            DbType.Int64 => $"{rowValue}",
            DbType.Double => $"{rowValue}",
            DbType.Int32 => $"{rowValue}",
            DbType.Single => $"{rowValue}",
            DbType.Int16 => $"{rowValue}",
            DbType.Byte => $"{rowValue}",
            DbType.Guid => $"new Guid((string){rowValue})",
            DbType.DateTimeOffset => $"DateTimeOffset.Parse((string){rowValue})",
            _ => $"\"{rowValue}\"",
        };
    }

    private async Task<string> RenderTemplate(string templateName, object modelData)
    {
        Template template = await ParseTemplate(templateName, Assembly.GetExecutingAssembly(), GetType());
        return template.Render(Hash.FromAnonymousObject(new
        {
            model = modelData,
        }));
    }

    private static void RegisterTypes()
    {
        Template.RegisterSafeType(typeof(Type),
                    typeof(Type).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(DataHelperConfiguration),
                    typeof(DataHelperConfiguration).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(Flavors),
            typeof(Flavors).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(TableBindingModel),
            typeof(TableDefinition).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(ColumnBindingModel),
            typeof(ColumnBindingModel).GetProperties().Select(p => p.Name).ToArray());
        Template.RegisterSafeType(typeof(ColumnDefinition),
            typeof(ColumnDefinition).GetProperties().Select(p => p.Name).ToArray());
    }

    /// <summary>
    /// Parse Template.
    /// </summary>
    /// <param name="templateName">Template name.</param>
    /// <param name="assembly">Executing assembly.</param>
    /// <param name="type">object type.</param>
    /// <returns>Template.</returns>
    private static async Task<Template> ParseTemplate(string templateName, Assembly assembly, Type type)
    {
        Stream resourceStream = assembly.GetManifestResourceStream(type, $"Templates.{templateName}.liquid");
        using StreamReader reader = new(resourceStream);
        string modelTemplate = await reader.ReadToEndAsync();
        return Template.Parse(modelTemplate);
    }
}
