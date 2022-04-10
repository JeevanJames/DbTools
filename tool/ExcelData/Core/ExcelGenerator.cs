﻿using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

using Datask.Common.Utilities;
using Datask.Providers;
using Datask.Providers.Schemas;
using Datask.Providers.SqlServer;
using Datask.Tool.ExcelData.Core.Bases;

using OfficeOpenXml;
using OfficeOpenXml.DataValidation.Contracts;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;

namespace Datask.Tool.ExcelData.Core;

public sealed class ExcelGenerator : Executor<ExcelGeneratorOptions, StatusEvents>
{
    private readonly ExcelGeneratorOptions _options;

    public ExcelGenerator(ExcelGeneratorOptions options)
        : base(options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public override async Task ExecuteAsync()
    {
        using ExcelPackage package = new(_options.ExcelFilePath);
        await FillExcelData(package.Workbook).ConfigureAwait(false);
        package.Save();
    }

    private async Task FillExcelData(ExcelWorkbook workbook)
    {
        IProvider provider = new SqlServerProvider(_options.ConnectionString);
        TableDefinitionCollection tables = await provider.SchemaQuery.GetTables(new GetTableOptions
        {
            IncludeColumns = true,
            IncludeForeignKeys = true,
        });
        tables.SortByForeignKeyDependencies();

        foreach (TableDefinition table in tables)
        {
            FireStatusEvent(StatusEvents.Generate,
                "Getting database table {Table} information.",
                new { Table = $"{table.Schema}.{table.Name}" });

            // Try creating the worksheet for the table, if it doesn't already exist.
            if (!TryCreateWorksheet(workbook, table, out ExcelWorksheet? worksheet))
                continue;

            int columnIndex = 1;
            foreach (ColumnDefinition column in table.Columns.Where(c => !c.IsAutoGenerated))
            {
                worksheet.Cells[1, columnIndex].Value = column.Name;
                worksheet.Cells[1, columnIndex].Style.Font.Bold = true;
                worksheet.Cells[1, columnIndex].AutoFitColumns();

                ApplyDataValidations(worksheet, columnIndex, table);
                AddColumnMetadata(worksheet, columnIndex, table);

                columnIndex++;
            }

            //Defining the tables parameters
            CreateExcelTable(worksheet, table);
        }
    }

    private static bool TryCreateWorksheet(ExcelWorkbook workbook, TableDefinition table,
        [NotNullWhen(true)] out ExcelWorksheet? worksheet)
    {
        if (workbook.Worksheets.SelectMany(ws => ws.Tables).Any(tbl => tbl.Name == table.FullName))
        {
            worksheet = null;
            return false;
        }

        string tableFullName = $"{table.Schema}.{table.Name}";
        string worksheetName;
        if (tableFullName.Length > 31)
        {
            Random random = new((int)DateTime.Now.Ticks);
            worksheetName = $"{tableFullName[..24]}...{random.Next(1, 99)}";
        }
        else
            worksheetName = tableFullName;

        worksheet = workbook.Worksheets.Add(worksheetName);
        return true;
    }

    private static void ApplyDataValidations(ExcelWorksheet worksheet, int columnIndex, TableDefinition table)
    {
        ColumnDefinition column = table.Columns[columnIndex - 1];

        string columnDataRange = ExcelCellBase.GetAddress(2, columnIndex, ExcelPackage.MaxRows, columnIndex);

        //Primary Key data validation
        //string columnLetter = columnDataRange.Split(':').First().Substring(0, 1);

        //if (tableInfo.Columns[i].IsPrimaryKey && !tableInfo.Columns[i].IsForeignKey)
        //{
        //    var pkCustomDataValidation = worksheet.DataValidations.AddCustomValidation(columnDataRange);

        //    //string columnLetter = columnDataRange.Split(':').First().Substring(0, 1);

        //    string pkValidationFormula = $"=COUNTIF(${columnLetter}2:${columnLetter}{ExcelPackage.MaxRows},{columnLetter}2)=1";

        //    //string pkValidationFormula = $"=COUNTIF(${columnLetter}:${columnLetter}{ExcelPackage.MaxRows},{columnLetter})=1, ISNUMBER(${columnLetter})";
        //    pkCustomDataValidation.ShowErrorMessage = true;
        //    pkCustomDataValidation.Error =
        //        $"Duplicate values are not allowed, its Primary Key.";
        //    pkCustomDataValidation.Formula.ExcelFormula = pkValidationFormula;
        //}

        if (column.IsForeignKey && !column.IsPrimaryKey)
        {
            string foreignKeyTableName = $"{column.ForeignKey.Schema}.{column.ForeignKey.Table}";
            ExcelTable? excelTable = worksheet.Workbook.Worksheets.SelectMany(ws => ws.Tables)
                .FirstOrDefault(t => t.Name == foreignKeyTableName);

            if (excelTable is not null)
            {
                int? fkColumnPosition = excelTable.Columns[column.ForeignKey.Column]?.Id;
                if (fkColumnPosition is not null)
                {
                    //var fkCellRange = ExcelRange.GetAddress(2, i, ExcelPackage.MaxRows, i);
                    IExcelDataValidationList? fkDataValidation =
                        worksheet.DataValidations.AddListValidation(columnDataRange);
                    string fkColumnLetter = ExcelCellAddress.GetColumnLetter((int)fkColumnPosition);
                    string validationFormula =
                        $"='{foreignKeyTableName}'!${fkColumnLetter}$2:${fkColumnLetter}${ExcelPackage.MaxRows}";

                    fkDataValidation.ShowErrorMessage = true;
                    fkDataValidation.Error = "The value cannot be empty.";
                    fkDataValidation.Formula.ExcelFormula = validationFormula;
                }
            }
        }

        switch (column.ClrType)
        {
            case { } dt when dt == typeof(DateTime) || dt == typeof(DateTimeOffset):
                worksheet.Column(columnIndex).Style.Numberformat.Format = DateTimeFormatInfo.CurrentInfo.ShortDatePattern;
                break;

            case { } ts when ts == typeof(TimeSpan):
                worksheet.Column(columnIndex).Style.Numberformat.Format = DateTimeFormatInfo.CurrentInfo.ShortTimePattern;
                break;

            case { } b when b == typeof(bool):
                AddBooleanValidation(worksheet, columnDataRange);
                break;

            case { } s when s == typeof(string) && column.MaxLength > 0:
                AddStringLengthValidation(worksheet, columnDataRange);
                break;

            case { } intType when intType == typeof(int) && !column.IsForeignKey:
                AddIntegerValidation(worksheet, columnDataRange);
                break;
        }

        static void AddBooleanValidation(ExcelWorksheet worksheet, string columnDataRange)
        {
            //Add data validations for bit values 0/1
            IExcelDataValidationList? bitDataValidation = worksheet.DataValidations.AddListValidation(columnDataRange);

            bitDataValidation.Formula.Values.Add("0");
            bitDataValidation.Formula.Values.Add("1");
        }

        static void AddStringLengthValidation(ExcelWorksheet worksheet, string columnDataRange)
        {
            //IExcelDataValidationInt? stringLenValidation = worksheet.DataValidations.AddTextLengthValidation(columnDataRange);
            //stringLenValidation.ShowErrorMessage = true;
            //stringLenValidation.ErrorStyle = ExcelDataValidationWarningStyle.stop;
            //stringLenValidation.ErrorTitle = "The value you entered is not valid";
            //stringLenValidation.Error =
            //    $"This cell must be between 0 and {columnDefn.MaxLength} characters in length.";
            //stringLenValidation.Formula.Value = 0;
            //stringLenValidation.Formula2.Value = columnDefn.MaxLength;
        }

        static void AddIntegerValidation(ExcelWorksheet worksheet, string columnDataRange)
        {
            //Add data validations for integer
            IExcelDataValidationInt? intDataValidation = worksheet.DataValidations.AddIntegerValidation(columnDataRange);

            intDataValidation.ShowErrorMessage = true;
            intDataValidation.Error = "The value must be an integer.";
            intDataValidation.Formula.Value = 0;
            intDataValidation.Formula2.Value = int.MaxValue;
        }
    }

    private static void AddColumnMetadata(ExcelWorksheet worksheet, int columnIndex, TableDefinition tableInfo)
    {
        ColumnDefinition column = tableInfo.Columns[columnIndex - 1];
        var metadata = new
        {
            column.Name,
            NativeType = column.DatabaseType,
            Type = TypeDisplay.GetTypeName(column.ClrType, "System"),
            DbType = column.DbType.ToString(),
            column.MaxLength,
            column.IsPrimaryKey,
            column.IsIdentity,
            column.IsNullable,
            column.IsAutoGenerated,
            column.IsForeignKey,
            ForeignKey = new
            {
                Schema = column.IsForeignKey ? column.ForeignKey.Schema : string.Empty,
                Table = column.IsForeignKey ? column.ForeignKey.Table : string.Empty,
                Column = column.IsForeignKey ? column.ForeignKey.Column : string.Empty,
            }
        };

        string serializedMetadata = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        ExcelComment comment = worksheet.Cells[1, columnIndex].AddComment(serializedMetadata, "Owner");
        comment.AutoFit = true;
    }

    private static void CreateExcelTable(ExcelWorksheet worksheet, TableDefinition table)
    {
        ExcelRange tableRange = worksheet.Cells[1, 1, worksheet.Dimension.End.Row, worksheet.Dimension.End.Column];
        tableRange.Style.Border.Top.Style = ExcelBorderStyle.Medium;
        tableRange.Style.Border.Left.Style = ExcelBorderStyle.Medium;
        tableRange.Style.Border.Right.Style = ExcelBorderStyle.Medium;
        tableRange.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;

        //Adding a table to a Range
        ExcelTable excelTable = worksheet.Tables.Add(tableRange, $"{table.Schema}.{table.Name}");

        //Formatting the table style
        excelTable.TableStyle = TableStyles.Dark10;
    }
}
