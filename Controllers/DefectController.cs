using DefectModify.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DefectModify.Controllers
{
    public class DefectController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DefectController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ============================================================
        //  GET: /Defect/Modify
        // ============================================================
        public async Task<IActionResult> Modify(
            string? dateFrom, string? dateTo,
            string? itemCode, string? operation,
            string? table = "history")
        {
            ViewBag.DateFrom   = dateFrom;
            ViewBag.DateTo     = dateTo;
            ViewBag.ItemCode   = itemCode;
            ViewBag.Operation  = operation;
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
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditHistory(SVN_Defect_Record_History model)
        {
            var existing = await _context.SVN_Defect_Record_Histories.FindAsync(model.Id);
            if (existing == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), new { table = "history" });
            }

            // Capture old values for log
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
            existing.Time_line     = DateTime.Now;  // track modification time

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

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã cập nhật bản ghi #{model.Id} thành công!";
            return RedirectToAction(nameof(Modify), new { table = "history" });
        }

        // ============================================================
        //  POST: Delete SVN_Defect_Record_History
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistory(int id)
        {
            var record = await _context.SVN_Defect_Record_Histories.FindAsync(id);
            if (record == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), new { table = "history" });
            }

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

            TempData["Success"] = $"Đã xóa bản ghi #{id} thành công!";
            return RedirectToAction(nameof(Modify), new { table = "history" });
        }

        // ============================================================
        //  POST: Edit SVN_Defect_Record  (no single PK → raw SQL)
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditRecord(
            SVN_Defect_Record model,
            string origItemCode, string origDefectCode,
            string origInsDatetime, string origOperation)
        {
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
            return RedirectToAction(nameof(Modify), new { table = "record" });
        }

        // ============================================================
        //  POST: Delete SVN_Defect_Record
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRecord(
            string itemCode, string defectCode,
            string insDatetime, string operation)
        {
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
            return RedirectToAction(nameof(Modify), new { table = "record" });
        }

        // ============================================================
        //  GET: /Defect/Log
        // ============================================================
        public async Task<IActionResult> Log(
            string? action, string? tableName,
            string? dateFrom, string? dateTo)
        {
            ViewBag.Action    = action;
            ViewBag.TableName = tableName;
            ViewBag.DateFrom  = dateFrom;
            ViewBag.DateTo    = dateTo;

            var query = _context.DefectEditLogs.AsQueryable();

            if (!string.IsNullOrEmpty(action))
                query = query.Where(l => l.Action == action);
            if (!string.IsNullOrEmpty(tableName))
                query = query.Where(l => l.TableName == tableName);
            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
                query = query.Where(l => l.ModifiedAt >= df);
            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
                query = query.Where(l => l.ModifiedAt < dt.AddDays(1));

            var logs = await query.OrderByDescending(l => l.ModifiedAt).Take(500).ToListAsync();
            return View(logs);
        }
    }
}