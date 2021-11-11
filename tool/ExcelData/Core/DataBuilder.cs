﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using CodeBits;

using Datask.Common.Utilities;
using Datask.Providers;
using Datask.Providers.SqlServer;
using Datask.Tool.ExcelData.Core.Events;

using OfficeOpenXml;
using OfficeOpenXml.DataValidation.Contracts;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;

namespace Datask.Tool.ExcelData.Core
{
    public sealed class DataBuilder
    {
#pragma warning disable S3264 // Events should be invoked
        public event EventHandler<StatusEventArgs<StatusEvents>> OnStatus = null!;
#pragma warning restore S3264 // Events should be invoked

        private readonly DataConfiguration _configuration;

        public DataBuilder(DataConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task ExportExcel()
        {
            using ExcelPackage package = new(_configuration.FilePath);
            await FillExcelData(package.Workbook).ConfigureAwait(false);
            package.Save();
        }

        private async Task FillExcelData(ExcelWorkbook workbook)
        {
            IProvider provider = new SqlServerProvider(_configuration.ConnectionString);
            List<TableDefinition> tables = await provider.SchemaQuery.EnumerateTables(new EnumerateTableOptions
            {
                IncludeColumns = true, IncludeForeignKeys = true,
            }).ToListAsync();
            Sort(tables);

            foreach (TableDefinition table in tables)
            {
                OnStatus.Fire(StatusEvents.Generate,
                    new { Table = table.FullName },
                    "Getting database table {Table} information.");

                if (!TryCreateWorksheet(workbook, table, out ExcelWorksheet? worksheet))
                    continue;

                for (int i = 0; i < table.Columns.Count; i++)
                {
                    worksheet.Column(i + 1).
                    worksheet.Cells[1, i + 1].Value = table.Columns[i].Name;
                    worksheet.Cells[1, i + 1].Style.Font.Bold = true;
                    worksheet.Cells[1, i + 1].AutoFitColumns();

                    ApplyDataValidations(worksheet, i + 1, table);
                    AddColumnMetaData(worksheet, i + 1, table);
                }

                //Defining the tables parameters
                CreateExcelTable(worksheet, table);
            }
        }

        private static void Sort(IList<TableDefinition> tables)
        {
            TableForeignKeyComparer comparer = new();
            for (int i = 0; i < tables.Count - 1; i++)
            {
                for (int j = i + 1; j < tables.Count; j++)
                {
                    if (comparer.Compare(tables[i], tables[j]) > 0)
                    {
                        (tables[i], tables[j]) = (tables[j], tables[i]);
                    }
                }
            }
        }

        private static bool TryCreateWorksheet(ExcelWorkbook workbook, TableDefinition table,
            [NotNullWhen(true)] out ExcelWorksheet? worksheet)
        {
            if (workbook.Worksheets.Any(ws => ws.Tables.Any(tbl => tbl.Name == table.FullName)))
            {
                worksheet = null;
                return false;
            }

            Random random = new((int)DateTime.Now.Ticks);
            string worksheetName = table.FullName.Length > 31
                ? $"{table.FullName[..24]}...{random.Next(1, 99)}"
                : table.FullName;

            worksheet = workbook.Worksheets.Add(worksheetName);
            return true;
        }

        private static void ApplyDataValidations(ExcelWorksheet worksheet, int columnIndex, TableDefinition tableInfo)
        {
            ColumnDefinition columnDefn = tableInfo.Columns[columnIndex - 1];

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

            if (columnDefn.IsForeignKey && !columnDefn.IsPrimaryKey)
            {
                foreach (ExcelWorksheet sheet in worksheet.Workbook.Worksheets)
                {
                    foreach (ExcelTable table in sheet.Tables)
                    {
                        if (table.Name != columnDefn.ForeignKey.Table)
                            continue;

                        int? fkColumnPosition = table.Columns[columnDefn.ForeignKey.Column]?.Id;
                        if (fkColumnPosition is null)
                            continue;

                        //var fkCellRange = ExcelRange.GetAddress(2, i, ExcelPackage.MaxRows, i);
                        IExcelDataValidationList? fkDataValidation = worksheet.DataValidations.AddListValidation(columnDataRange);
                        string fkColumnLetter = ExcelCellAddress.GetColumnLetter((int)fkColumnPosition);
                        string validationFormula =
                            $"='{columnDefn.ForeignKey.Table}'!${fkColumnLetter}$2:${fkColumnLetter}${ExcelPackage.MaxRows}";

                        fkDataValidation.ShowErrorMessage = true;
                        fkDataValidation.Error = "The value cannot be empty.";
                        fkDataValidation.Formula.ExcelFormula = validationFormula;
                    }
                }
            }

            switch (columnDefn.Type)
            {
                case Type dt when dt == typeof(DateTime):
                case Type dto when dto == typeof(DateTimeOffset):
                    worksheet.Column(columnIndex).Style.Numberformat.Format = DateTimeFormatInfo.CurrentInfo.ShortDatePattern;
                    break;

                case Type ts when ts == typeof(TimeSpan):
                    worksheet.Column(columnIndex).Style.Numberformat.Format = DateTimeFormatInfo.CurrentInfo.ShortTimePattern;
                    break;

                case Type b when b == typeof(bool):
                    AddBooleanValidation(worksheet, columnDataRange);
                    break;

                case Type s when s == typeof(string) && columnDefn.MaxLength > 0:
                    AddStringLengthValidation(worksheet, columnDataRange);
                    break;

                case Type intType when intType == typeof(int) && !columnDefn.IsForeignKey:
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

        private static void AddColumnMetaData(ExcelWorksheet worksheet, int columnIndex, TableDefinition tableInfo)
        {
            ColumnDefinition column = tableInfo.Columns[columnIndex - 1];
            var metadata = new
            {
                column.Name,
                NativeType = column.DatabaseType,
                Type = TypeDisplay.GetTypeName(column.Type, "System"),
                DbType = column.DbType.ToString(),
                column.MaxLength,
                column.IsPrimaryKey,
                column.IsIdentity,
                column.IsNullable,
                column.IsForeignKey,
            };

            string serializedMetadata = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            ExcelComment colComment = worksheet.Cells[1, columnIndex].AddComment(serializedMetadata, "Owner");
            colComment.AutoFit = true;
        }

        private static void CreateExcelTable(ExcelWorksheet worksheet, TableDefinition table)
        {
            ExcelRange tableRange = worksheet.Cells[1, 1, worksheet.Dimension.End.Row, worksheet.Dimension.End.Column];
            tableRange.Style.Border.Top.Style = ExcelBorderStyle.Medium;
            tableRange.Style.Border.Left.Style = ExcelBorderStyle.Medium;
            tableRange.Style.Border.Right.Style = ExcelBorderStyle.Medium;
            tableRange.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;

            //Adding a table to a Range
            ExcelTable excelTable = worksheet.Tables.Add(tableRange, table.Name);

            //Formatting the table style
            excelTable.TableStyle = TableStyles.Dark10;
        }
    }
}
