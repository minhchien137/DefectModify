using DefectModify.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace DefectModify.Controllers
{
    public class DefectController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly string _connStr;

        public DefectController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _connStr = configuration.GetConnectionString("DefaultConnection")!;
        }

        // ============================================================
        //  HELPER: Đồng bộ SVN_Defect_Record từ History theo nhóm key
        //  Group key: (Item_code, Defect_Code, INSDatetime, Operation)
        //  - Nếu còn history → UPDATE tổng Qty_NG vào Record
        //  - Nếu không còn history nào → DELETE Record tương ứng
        // ============================================================
        private async Task SyncRecordFromHistory(
            string? itemCode, string? defectCode,
            string? insDatetime, string? operation)
        {
            // Tính tổng Qty_NG từ tất cả history records cùng nhóm
            var totalQty = await _context.SVN_Defect_Record_Histories
                .Where(h => h.Item_code   == itemCode
                         && h.Defect_Code == defectCode
                         && h.INSDatetime == insDatetime
                         && h.Operation   == operation)
                .SumAsync(h => (int?)h.Qty_NG) ?? 0;

            if (totalQty > 0)
            {
                // Còn history → UPDATE Qty_NG trong Record
                await _context.Database.ExecuteSqlRawAsync(
                    @"UPDATE SVN_Defect_Record
                      SET Qty_NG = {0}
                      WHERE Item_code = {1} AND Defect_Code = {2}
                        AND INSDatetime = {3} AND Operation = {4}",
                    totalQty.ToString(), itemCode, defectCode, insDatetime, operation);
            }
            else
            {
                // Không còn history nào → DELETE Record
                await _context.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM SVN_Defect_Record
                      WHERE Item_code = {0} AND Defect_Code = {1}
                        AND INSDatetime = {2} AND Operation = {3}",
                    itemCode, defectCode, insDatetime, operation);
            }
        }

        // ============================================================
        //  GET: /Defect/Modify
        // ============================================================
        public async Task<IActionResult> Modify(
            string? dateFrom, string? dateTo,
            string? itemCode, string? operation,
            string? table = "history")
        {
            ViewBag.DateFrom    = dateFrom;
            ViewBag.DateTo      = dateTo;
            ViewBag.ItemCode    = itemCode;
            ViewBag.Operation   = operation;
            ViewBag.ActiveTable = table ?? "history";

            // ---- Helper: convert "yyyy-MM-dd" → "yyyyMMdd" for string comparison ----
            string? fromStr = dateFrom?.Replace("-", "");
            string? toStr   = dateTo?.Replace("-", "");

            // ---- SVN_Defect_Record_History ----
            var histQ = _context.SVN_Defect_Record_Histories.AsQueryable();

            if (!string.IsNullOrEmpty(fromStr))
                histQ = histQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(fromStr) >= 0);
            if (!string.IsNullOrEmpty(toStr))
                histQ = histQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(toStr) <= 0);
            if (!string.IsNullOrEmpty(itemCode))
                histQ = histQ.Where(r => r.Item_code != null && r.Item_code.Contains(itemCode));
            if (!string.IsNullOrEmpty(operation))
                histQ = histQ.Where(r => r.Operation != null && r.Operation.Contains(operation));

            // ---- SVN_Defect_Record ----
            var recQ = _context.SVN_Defect_Records.AsQueryable();

            if (!string.IsNullOrEmpty(fromStr))
                recQ = recQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(fromStr) >= 0);
            if (!string.IsNullOrEmpty(toStr))
                recQ = recQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(toStr) <= 0);
            if (!string.IsNullOrEmpty(itemCode))
                recQ = recQ.Where(r => r.Item_code != null && r.Item_code.Contains(itemCode));
            if (!string.IsNullOrEmpty(operation))
                recQ = recQ.Where(r => r.Operation != null && r.Operation.Contains(operation));

            var vm = new DefectModifyViewModel
            {
                HistoryRecords = await histQ.OrderByDescending(r => r.INSDatetime).Take(300).ToListAsync(),
                DefectRecords  = await recQ.OrderByDescending(r => r.INSDatetime).Take(300).ToListAsync()
            };

            return View(vm);
        }

        // ============================================================
        //  POST: Edit SVN_Defect_Record_History
        //  Sau khi sửa History → đồng bộ lại Record
        //  Lưu ý: nếu đổi nhóm key (Item_code/Defect_Code/INSDatetime/Operation)
        //         thì phải sync cả nhóm CŨ lẫn nhóm MỚI
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditHistory(SVN_Defect_Record_History model,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "history",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };

            var existing = await _context.SVN_Defect_Record_Histories.FindAsync(model.Id);
            if (existing == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // Lưu lại nhóm key CŨ trước khi thay đổi
            var oldItemCode   = existing.Item_code;
            var oldDefectCode = existing.Defect_Code;
            var oldInsDate    = existing.INSDatetime;
            var oldOperation  = existing.Operation;

            // Capture old values for audit log
            string oldValues = JsonSerializer.Serialize(new
            {
                existing.Work_order, existing.Item_code, existing.Defect_Code,
                existing.Defect_Name, existing.Qty_NG, existing.INSDatetime,
                existing.Operation, existing.Employer_code, existing.Employer_name, existing.Note
            });

            // Apply changes
            existing.Work_order    = model.Work_order;
            existing.Item_code     = model.Item_code;
            existing.Defect_Code   = model.Defect_Code;
            existing.Defect_Name   = model.Defect_Name;
            existing.Qty_NG        = model.Qty_NG;
            existing.INSDatetime   = model.INSDatetime;
            existing.Operation     = model.Operation;
            existing.Employer_code = model.Employer_code;
            existing.Employer_name = model.Employer_name;
            existing.Note          = model.Note;
            // Time_line giữ nguyên, không ghi đè

            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record_History",
                Action     = "Edit",
                RecordId   = model.Id.ToString(),
                OldValues  = oldValues,
                NewValues  = JsonSerializer.Serialize(new
                {
                    model.Work_order, model.Item_code, model.Defect_Code,
                    model.Defect_Name, model.Qty_NG, model.INSDatetime,
                    model.Operation, model.Employer_code, model.Employer_name, model.Note
                }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            try { await _context.SaveChangesAsync(); }
            catch (Exception ex)
            {
                TempData["Error"] = $"Lỗi khi lưu: {ex.InnerException?.Message ?? ex.Message}";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // ---- Đồng bộ sang SVN_Defect_Record ----

            // Kiểm tra nhóm key có thay đổi không
            bool keyChanged = oldItemCode   != model.Item_code
                           || oldDefectCode != model.Defect_Code
                           || oldInsDate    != model.INSDatetime
                           || oldOperation  != model.Operation;

            if (keyChanged)
            {
                // Sync nhóm CŨ (bị mất 1 history → tính lại / xóa nếu hết)
                await SyncRecordFromHistory(oldItemCode, oldDefectCode, oldInsDate, oldOperation);
            }

            // Sync nhóm MỚI (luôn cần cập nhật tổng)
            await SyncRecordFromHistory(model.Item_code, model.Defect_Code, model.INSDatetime, model.Operation);

            TempData["Success"] = $"Đã cập nhật bản ghi #{model.Id} và đồng bộ bản ghi lỗi thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Delete SVN_Defect_Record_History
        //  Sau khi xóa History → tính lại tổng nhóm đó trong Record
        //  Nếu không còn history nào → DELETE Record luôn
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistory(int id,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "history",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };

            var record = await _context.SVN_Defect_Record_Histories.FindAsync(id);
            if (record == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // Lưu lại nhóm key trước khi xóa để sync sau
            var itemCode   = record.Item_code;
            var defectCode = record.Defect_Code;
            var insDate    = record.INSDatetime;
            var operation  = record.Operation;

            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record_History",
                Action     = "Delete",
                RecordId   = id.ToString(),
                OldValues  = JsonSerializer.Serialize(record),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            _context.SVN_Defect_Record_Histories.Remove(record);
            await _context.SaveChangesAsync();

            // ---- Đồng bộ sang SVN_Defect_Record ----
            // SyncRecordFromHistory sẽ tự xóa Record nếu tổng = 0
            await SyncRecordFromHistory(itemCode, defectCode, insDate, operation);

            TempData["Success"] = $"Đã xóa bản ghi #{id} và đồng bộ bản ghi lỗi thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Edit SVN_Defect_Record  (no single PK → raw SQL)
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditRecord(
            SVN_Defect_Record model,
            string origItemCode, string origDefectCode,
            string origInsDatetime, string origOperation,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "record",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };
            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record",
                Action     = "Edit",
                RecordId   = $"{origItemCode}|{origDefectCode}|{origInsDatetime}|{origOperation}",
                OldValues  = JsonSerializer.Serialize(new { origItemCode, origDefectCode, origInsDatetime, origOperation }),
                NewValues  = JsonSerializer.Serialize(new { model.Qty_NG, model.Employer_code, model.Employer_name }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            await _context.Database.ExecuteSqlRawAsync(
                @"UPDATE SVN_Defect_Record
                  SET Qty_NG = {0}, Employer_code = {1}, Employer_name = {2}
                  WHERE Item_code = {3} AND Defect_Code = {4}
                    AND INSDatetime = {5} AND Operation = {6}",
                model.Qty_NG, model.Employer_code, model.Employer_name,
                origItemCode, origDefectCode, origInsDatetime, origOperation);

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã cập nhật bản ghi {origItemCode} / {origDefectCode} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Delete SVN_Defect_Record
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRecord(
            string itemCode, string defectCode,
            string insDatetime, string operation,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "record",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };
            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record",
                Action     = "Delete",
                RecordId   = $"{itemCode}|{defectCode}|{insDatetime}|{operation}",
                OldValues  = JsonSerializer.Serialize(new { itemCode, defectCode, insDatetime, operation }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM SVN_Defect_Record
                  WHERE Item_code = {0} AND Defect_Code = {1}
                    AND INSDatetime = {2} AND Operation = {3}",
                itemCode, defectCode, insDatetime, operation);

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã xóa bản ghi {itemCode} / {defectCode} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  GET: /Defect/Log  — ADO.NET thuần, không qua EF
        // ============================================================
        public async Task<IActionResult> Log(
            string? action, string? tableName,
            string? dateFrom, string? dateTo)
        {
            ViewBag.Action    = action;
            ViewBag.TableName = tableName;
            ViewBag.DateFrom  = dateFrom;
            ViewBag.DateTo    = dateTo;

            var logs = new List<DefectEditLog>();

            var wheres = new List<string>();
            if (!string.IsNullOrEmpty(action))     wheres.Add("Action = @action");
            if (!string.IsNullOrEmpty(tableName))  wheres.Add("TableName = @tableName");
            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out _))
                wheres.Add("ModifiedAt >= @dateFrom");
            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out _))
                wheres.Add("ModifiedAt < @dateTo");

            string whereClause = wheres.Any() ? "WHERE " + string.Join(" AND ", wheres) : "";
            string sql = $"SELECT TOP 500 Id, TableName, Action, RecordId, OldValues, NewValues, ModifiedAt, ModifiedBy FROM DefectEditLog {whereClause} ORDER BY ModifiedAt DESC";

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);

            if (!string.IsNullOrEmpty(action))    cmd.Parameters.AddWithValue("@action",    action);
            if (!string.IsNullOrEmpty(tableName)) cmd.Parameters.AddWithValue("@tableName", tableName);
            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
                cmd.Parameters.AddWithValue("@dateFrom", df);
            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
                cmd.Parameters.AddWithValue("@dateTo", dt.AddDays(1));

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(new DefectEditLog
                {
                    Id         = reader.GetInt32(0),
                    TableName  = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Action     = reader.IsDBNull(2) ? null : reader.GetString(2),
                    RecordId   = reader.IsDBNull(3) ? null : reader.GetString(3),
                    OldValues  = reader.IsDBNull(4) ? null : reader.GetString(4),
                    NewValues  = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ModifiedAt = reader.GetDateTime(6),
                    ModifiedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }

            return View(logs);
        }
    }
}