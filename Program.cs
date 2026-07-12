using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerrariaSaveConverter
{
    public enum Platform { Unknown, International, China }

    public static class Logger
    {
        private static readonly string LogFile = "Converter_Debug.log";
        private static readonly object _lock = new object();
        public static bool EnableVerbose { get; set; } = false;

        public static void Init()
        {
            if (!EnableVerbose) return;
            try
            {
                File.WriteAllText(LogFile, $"--- Terraria Save Converter Log Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n", Encoding.UTF8);
            }
            catch { }
        }

        public static void Info(string msg, bool writeLine = true) => Log("INFO", msg, writeLine, ConsoleColor.White);
        public static void Warn(string msg, bool writeLine = true) => Log("WARN", msg, writeLine, ConsoleColor.Yellow);
        public static void Error(string msg, bool writeLine = true) => Log("ERROR", msg, writeLine, ConsoleColor.Red);
        public static void Success(string msg, bool writeLine = true) => Log("SUCCESS", msg, writeLine, ConsoleColor.Green);
        public static void Verbose(string msg, bool writeLine = true)
        {
            if (!EnableVerbose) return;
            Log("DEBUG", msg, writeLine, ConsoleColor.DarkGray);
        }

        private static void Log(string level, string msg, bool writeLine, ConsoleColor color)
        {
            if (level == "DEBUG" && !EnableVerbose) return;

            Console.ForegroundColor = color;
            if (writeLine) Console.WriteLine(msg); else Console.Write(msg);
            Console.ResetColor();

            if (EnableVerbose)
            {
                try
                {
                    lock (_lock)
                    {
                        string time = DateTime.Now.ToString("HH:mm:ss.fff");
                        string fileMsg = writeLine ? $"[{time}][{level}] {msg}\n" : msg;
                        File.AppendAllText(LogFile, fileMsg, Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
    }

    class Program
    {
        private static readonly byte[] ENCRYPTION_KEY = Encoding.Unicode.GetBytes("h3y_gUyZ");
        private const ulong INTL_MAGIC = 0x006369676F6C6572; // "cigoler"
        private const ulong CN_MAGIC = 0x00676E6F646E6978; // "gnodnix"

        private static int INTL_TILE_COUNT = 625;
        private static int INTL_WALL_COUNT = 316;

        static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.UTF8;

            var rawArgs = args.ToList();
            if (rawArgs.Contains("-v") || rawArgs.Contains("--verbose"))
            {
                Logger.EnableVerbose = true;
                rawArgs.Remove("-v");
                rawArgs.Remove("--verbose");
            }

            Logger.Init();

            if (rawArgs.Count < 2)
            {
                Console.WriteLine("==================================================");
                Console.WriteLine("  泰拉瑞亚 存档全自动双向 ZIP 转换工具");
                Console.WriteLine("==================================================");
                Console.WriteLine("用法: TerrariaSaveConverter <输入路径或ZIP包> <目标平台(cn/intl)> [-v|--verbose]");
                return;
            }

            if (Logger.EnableVerbose)
            {
                Logger.Info("==================================================");
                Logger.Info("  泰拉瑞亚 存档全自动双向 ZIP 转换工具");
                Logger.Info("==================================================");
            }

            TryLoadIntlConstants(out INTL_TILE_COUNT, out INTL_WALL_COUNT);

            string inputPath = rawArgs[0];
            string targetStr = rawArgs[1].ToLower();
            Platform targetPlatform = targetStr == "cn" ? Platform.China : Platform.International;

            if (targetPlatform == Platform.Unknown || !File.Exists(inputPath))
            {
                Logger.Error($"目标平台无效，或未找到指定输入路径: {inputPath}");
                return;
            }

            string ext = Path.GetExtension(inputPath).ToLower();
            string outputPath = inputPath.Replace(ext, $"_{targetStr}{ext}");

            try
            {
                if (ext == ".zip")
                    ProcessZipArchive(inputPath, outputPath, targetPlatform);
                else
                    ProcessSingleFile(inputPath, outputPath, targetPlatform);

                Logger.Success("\n🎉 转换流程全部顺利结束！");
            }
            catch (Exception ex)
            {
                Logger.Error($"发生致命错误: {ex.Message}");
                Logger.Verbose(ex.StackTrace ?? "");
            }
        }

        static void ProcessZipArchive(string inputZipPath, string outputZipPath, Platform targetPlatform)
        {
            Logger.Info($"\n📦 正在加载 ZIP 归档包: {Path.GetFileName(inputZipPath)}");
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            Encoding safeEncoding = DetectZipEncoding(inputZipPath);
            using ZipArchive sourceZip = ZipFile.Open(inputZipPath, ZipArchiveMode.Read, safeEncoding);
            using ZipArchive targetZip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create, Encoding.UTF8);

            foreach (ZipArchiveEntry entry in sourceZip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;

                string entryExt = Path.GetExtension(entry.Name).ToLower();
                bool isSaveFile = entryExt == ".plr" || entryExt == ".wld" || entryExt == ".map" || entry.Name.Contains(".bak");

                if (!isSaveFile)
                {
                    ZipArchiveEntry newEntry = targetZip.CreateEntry(entry.FullName);
                    using Stream sIn = entry.Open();
                    using Stream sOut = newEntry.Open();
                    sIn.CopyTo(sOut);
                    continue;
                }

                byte[] fileBytes;
                using (Stream entryStream = entry.Open())
                using (MemoryStream ms = new MemoryStream())
                {
                    entryStream.CopyTo(ms);
                    fileBytes = ms.ToArray();
                }

                Logger.Info($"  -> [正在处理] {entry.FullName} ", false);
                byte[] processedBytes = ConvertSavePayload(fileBytes, entry.Name, targetPlatform);

                ZipArchiveEntry targetEntry = targetZip.CreateEntry(entry.FullName);
                using Stream outStream = targetEntry.Open();
                outStream.Write(processedBytes, 0, processedBytes.Length);
            }
        }

        static void ProcessSingleFile(string inputPath, string outputPath, Platform targetPlatform)
        {
            byte[] rawBytes = File.ReadAllBytes(inputPath);
            Logger.Info($"正在处理文件 {Path.GetFileName(inputPath)}... ", false);
            byte[] processedBytes = ConvertSavePayload(rawBytes, Path.GetFileName(inputPath), targetPlatform);
            File.WriteAllBytes(outputPath, processedBytes);
        }

        static byte[] ConvertSavePayload(byte[] fileBytes, string fileName, Platform targetPlatform)
        {
            string fileNameLower = fileName.ToLower();
            bool isEncrypted = fileNameLower.EndsWith(".plr") || fileNameLower.EndsWith(".plr.bak");
            
            if (isEncrypted) Logger.Verbose($"\n    [AES 解密] 目标文件受加密保护，正在解密...");
            byte[] processData = isEncrypted ? DecryptAES(fileBytes) : fileBytes;
            
            if (processData.Length < 12) return fileBytes;

            using MemoryStream ms = new MemoryStream(processData);
            using BinaryReader br = new BinaryReader(ms);

            int versionRaw = br.ReadInt32();
            bool isCompressed = (versionRaw & 0x8000) == 0x8000;
            int trueVersion = versionRaw & ~0x8000;
            bool hasMetadata = trueVersion >= 135;

            Logger.Verbose($"\n    [文件解析] Version: {trueVersion}, isCompressed: {isCompressed}, hasMetadata: {hasMetadata}");

            if (fileNameLower.EndsWith(".map") || fileNameLower.EndsWith(".map.bak"))
            {
                if (!isCompressed)
                {
                    Logger.Info("(已跳过: 远古未压缩地图)");
                    Logger.Verbose("    [拦截] 地图非 Zlib 压缩格式 (<= Version 91)，安全放行。");
                    return fileBytes;
                }

                Platform currentPlatform = Platform.Unknown;
                if (hasMetadata)
                {
                    ulong magic = br.ReadUInt64();
                    ulong baseMagic = magic & 0x00FFFFFFFFFFFFFF;
                    if (baseMagic == INTL_MAGIC) currentPlatform = Platform.International;
                    else if (baseMagic == CN_MAGIC) currentPlatform = Platform.China;
                }

                ms.Seek(0, SeekOrigin.Begin);
                processData = ProcessMapData(ms, currentPlatform, targetPlatform, hasMetadata);
            }
            else
            {
                if (!hasMetadata)
                {
                    Logger.Info("(已跳过: 无元数据)");
                    return fileBytes;
                }

                ulong magic = br.ReadUInt64();
                byte fileType = (byte)((magic >> 56) & 0xFF);
                ulong baseMagic = magic & 0x00FFFFFFFFFFFFFF;

                Platform currentPlatform = Platform.Unknown;
                if (baseMagic == INTL_MAGIC) currentPlatform = Platform.International;
                else if (baseMagic == CN_MAGIC) currentPlatform = Platform.China;

                if (currentPlatform == targetPlatform || currentPlatform == Platform.Unknown)
                {
                    Logger.Info("(已跳过: 平台匹配或未知格式)");
                    return fileBytes;
                }

                ulong newMagic = (targetPlatform == Platform.China ? CN_MAGIC : INTL_MAGIC) | ((ulong)fileType << 56);
                Logger.Verbose($"    [Magic Number] {baseMagic:X16} -> {(newMagic & 0x00FFFFFFFFFFFFFF):X16}");
                
                ms.Seek(4, SeekOrigin.Begin);
                using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    bw.Write(newMagic);
                }

                processData = ms.ToArray();
                Logger.Success("(Magic Number 切换完成)");
            }

            if (isEncrypted)
            {
                Logger.Verbose($"    [AES 加密] 重新加密数据流...");
                byte[] encryptedData = EncryptAES(processData);
                Array.Resize(ref encryptedData, fileBytes.Length);
                return encryptedData;
            }

            return processData;
        }

        static byte[] ProcessMapData(MemoryStream mapStream, Platform from, Platform to, bool hasMetadata)
        {
            using BinaryReader br = new BinaryReader(mapStream);
            int versionNum = br.ReadInt32();

            byte fileType = 3;
            uint revision = 0;
            ulong isFav = 0;

            if (hasMetadata)
            {
                ulong magic = br.ReadUInt64();
                fileType = (byte)((magic >> 56) & 0xFF);
                revision = br.ReadUInt32();
                isFav = br.ReadUInt64();
            }

            string mapName = br.ReadString();
            int worldId = br.ReadInt32();
            int maxY = br.ReadInt32();
            int maxX = br.ReadInt32();

            short num4 = br.ReadInt16();
            short num5 = br.ReadInt16();
            short num6 = br.ReadInt16();
            short num7 = br.ReadInt16();
            short num8 = br.ReadInt16();
            short num9 = br.ReadInt16();

            Logger.Verbose($"    [Map Header] 读取到文件头 TileCount: {num4}, WallCount: {num5}");
            Logger.Verbose($"    [Engine Info] 当前 PC 引擎上限 TileCount: {INTL_TILE_COUNT}, WallCount: {INTL_WALL_COUNT}");

            bool[] array3 = ReadBools(br, num4);
            bool[] array4 = ReadBools(br, num5);
            byte[] array5 = new byte[num4];
            for (int i = 0; i < num4; i++) array5[i] = array3[i] ? br.ReadByte() : (byte)1;
            
            byte[] array6 = new byte[num5];
            for (int i = 0; i < num5; i++) array6[i] = array4[i] ? br.ReadByte() : (byte)1;

            bool requiresChunkRewrite = (to == Platform.International) && (num4 > INTL_TILE_COUNT || num5 > INTL_WALL_COUNT);

            if (!requiresChunkRewrite)
            {
                using MemoryStream quickMs = new MemoryStream();
                quickMs.Write(mapStream.ToArray(), 0, (int)mapStream.Length);
                if (hasMetadata)
                {
                    quickMs.Seek(4, SeekOrigin.Begin);
                    using BinaryWriter quickBw = new BinaryWriter(quickMs, Encoding.UTF8, true);
                    ulong newMagic = (to == Platform.China ? CN_MAGIC : INTL_MAGIC) | ((ulong)fileType << 56);
                    quickBw.Write(newMagic);
                }
                Logger.Success("(无损转换: 字典已完美对齐，仅切换头部标识)");
                return quickMs.ToArray();
            }

            short newNum4 = (short)Math.Min(num4, INTL_TILE_COUNT);
            short newNum5 = (short)Math.Min(num5, INTL_WALL_COUNT);

            Logger.Verbose($"    [Truncate] 执行字典截断 -> Tile: {newNum4}, Wall: {newNum5}");

            Dictionary<ushort, ushort> dict = new Dictionary<ushort, ushort> { { 0, 0 } };
            ushort srcId = 1, dstId = 1;

            for (int i = 0; i < num4; i++)
            {
                bool isRetained = i < newNum4;
                for (int j = 0; j < array5[i]; j++) dict.Add(srcId++, isRetained ? dstId++ : (ushort)0);
            }
            for (int i = 0; i < num5; i++)
            {
                bool isRetained = i < newNum5;
                for (int j = 0; j < array6[i]; j++) dict.Add(srcId++, isRetained ? dstId++ : (ushort)0);
            }
            for (int i = 0; i < num6; i++) dict.Add(srcId++, dstId++);
            for (int i = 0; i < num7; i++) dict.Add(srcId++, dstId++);
            for (int i = 0; i < num8; i++) dict.Add(srcId++, dstId++);
            for (int i = 0; i < num9; i++) dict.Add(srcId++, dstId++);
            dict.Add(srcId++, dstId++); // 填补最后默认背景层缺失的坑

            Logger.Verbose($"    [Dict Build] 成功重构字典, File Options: {srcId - 1}, Target Options: {dstId - 1}");

            // ===== 逆向映射审计器 (仅在 Verbose 下初始化) =====
            Dictionary<ushort, string>? auditMap = null;
            if (Logger.EnableVerbose)
            {
                auditMap = new Dictionary<ushort, string>();
                ushort aId = 1;
                for (int i = 0; i < num4; i++)
                    for (int j = 0; j < array5[i]; j++) auditMap[aId++] = $"地砖(Tile) ID:{i}" + (array5[i] > 1 ? $" 变体:{j}" : "");
                for (int i = 0; i < num5; i++)
                    for (int j = 0; j < array6[i]; j++) auditMap[aId++] = $"墙壁(Wall) ID:{i}" + (array6[i] > 1 ? $" 变体:{j}" : "");
                for (int i = 0; i < num6; i++) auditMap[aId++] = $"天空层背景(Sky BG) 变体:{i}";
                for (int i = 0; i < num7; i++) auditMap[aId++] = $"泥土层背景(Dirt BG) 变体:{i}";
                for (int i = 0; i < num8; i++) auditMap[aId++] = $"岩石层背景(Rock BG) 变体:{i}";
                for (int i = 0; i < num9; i++) auditMap[aId++] = $"地狱层背景(Hell BG) 变体:{i}";
                auditMap[aId++] = "默认深渊背景";
            }

            using MemoryStream outMs = new MemoryStream();
            using BinaryWriter bw = new BinaryWriter(outMs, Encoding.UTF8, true);

            bw.Write(versionNum);
            if (hasMetadata)
            {
                ulong newMagic = INTL_MAGIC | ((ulong)fileType << 56);
                bw.Write(newMagic);
                bw.Write(revision);
                bw.Write(isFav);
            }

            bw.Write(mapName);
            bw.Write(worldId);
            bw.Write(maxY);
            bw.Write(maxX);
            bw.Write(newNum4);
            bw.Write(newNum5);
            bw.Write(num6);
            bw.Write(num7);
            bw.Write(num8);
            bw.Write(num9);

            bool[] newArray3 = new bool[newNum4];
            Array.Copy(array3, newArray3, newNum4);
            bool[] newArray4 = new bool[newNum5];
            Array.Copy(array4, newArray4, newNum5);
            byte[] newArray5 = new byte[newNum4];
            Array.Copy(array5, newArray5, newNum4);
            byte[] newArray6 = new byte[newNum5];
            Array.Copy(array6, newArray6, newNum5);

            WriteBools(bw, newNum4, newArray3);
            WriteBools(bw, newNum5, newArray4);
            for (int i = 0; i < newNum4; i++) if (newArray3[i]) bw.Write(newArray5[i]);
            for (int i = 0; i < newNum5; i++) if (newArray4[i]) bw.Write(newArray6[i]);

            int num18 = (maxX + 63) / 64;
            int num19 = (maxY + 63) / 64;
            int totalChunks = num18 * num19;
            int erasedCount = 0;
            Dictionary<ushort, int> erasedTypes = new Dictionary<ushort, int>();

            for (int l = 0; l < totalChunks; l++)
            {
                int compSize = br.ReadInt32();
                if (compSize > 0)
                {
                    byte[] compData = br.ReadBytes(compSize);
                    byte[] chunkBuf = DecompressZlib(compData, 16384);

                    for (int i = 0; i < 4096; i++)
                    {
                        int typeOffset = i * 4;
                        ushort type = BitConverter.ToUInt16(chunkBuf, typeOffset);

                        if (type > 0)
                        {
                            if (!dict.ContainsKey(type) || dict[type] == 0)
                            {
                                if (!erasedTypes.ContainsKey(type)) erasedTypes[type] = 0;
                                erasedTypes[type]++;
                                erasedCount++;

                                // 处理 ushort 下溢出风险：强制抹零 4-byte 避免变 .bad
                                chunkBuf[typeOffset] = 0;
                                chunkBuf[typeOffset + 1] = 0;
                                chunkBuf[typeOffset + 2] = 0;
                                chunkBuf[typeOffset + 3] = 0;
                            }
                            else
                            {
                                ushort newType = dict[type];
                                chunkBuf[typeOffset] = (byte)(newType & 0xFF);
                                chunkBuf[typeOffset + 1] = (byte)(newType >> 8);
                            }
                        }
                    }

                    byte[] newCompData = CompressZlib(chunkBuf);
                    bw.Write(newCompData.Length);
                    bw.Write(newCompData);
                }
                else
                {
                    bw.Write(0);
                }
            }

            Logger.Success($"(有损转换: 国服 -> 国际服, 字典对齐且越界数据已安全归零, 共擦除像素: {erasedCount})");
            
            if (erasedCount > 0 && Logger.EnableVerbose)
            {
                Logger.Verbose($"    [擦除审计] 详细越界像素抹除清单 ({erasedCount} 个):");
                foreach (var kvp in erasedTypes.OrderByDescending(k => k.Value))
                {
                    string desc = auditMap != null && auditMap.ContainsKey(kvp.Key) ? auditMap[kvp.Key] : "未知数据结构";
                    Logger.Verbose($"      - [Option {kvp.Key}] {desc} => 物理擦除 {kvp.Value} 个像素");
                }
            }

            bw.Flush(); // 解决截断写入引发的 EOF 越界异常
            return outMs.ToArray();
        }

        static void TryLoadIntlConstants(out int intlTileCount, out int intlWallCount)
        {
            intlTileCount = 753; 
            intlWallCount = 367;

            Logger.Verbose("\n🔍 正在通过 IL 静态解析引擎核心数据...");

            string? exePath = null;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                    string? steamPath = key?.GetValue("InstallPath")?.ToString();
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        string tPath = Path.Combine(steamPath, @"steamapps\common\Terraria\Terraria.exe");
                        if (File.Exists(tPath)) exePath = tPath;
                    }
                }
                catch { }

                if (exePath == null)
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                    {
                        string[] winPaths = {
                            Path.Combine(drive.Name, @"SteamLibrary\steamapps\common\Terraria\Terraria.exe"),
                            Path.Combine(drive.Name, @"Steam\steamapps\common\Terraria\Terraria.exe"),
                            Path.Combine(drive.Name, @"Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe")
                        };
                        exePath = winPaths.FirstOrDefault(File.Exists);
                        if (exePath != null) break;
                    }
                }
            }

            if (string.IsNullOrEmpty(exePath))
            {
                Logger.Verbose("    [拦截] 磁盘中未找到 PC 版 Terraria.exe 引擎，回退至安全默认上限。");
                return;
            }

            try
            {
                Logger.Success($"✅ 成功定位引擎: {exePath}");
                using ModuleDefinition module = ModuleDefinition.ReadModule(exePath);
                
                var tileIdType = module.Types.FirstOrDefault(t => t.FullName == "Terraria.ID.TileID");
                var wallIdType = module.Types.FirstOrDefault(t => t.FullName == "Terraria.ID.WallID");

                if (tileIdType != null && wallIdType != null)
                {
                    intlTileCount = GetFieldConstantFromIL(tileIdType, "Count");
                    intlWallCount = GetFieldConstantFromIL(wallIdType, "Count");
                    Logger.Success($"🎯 IL 穿透提取成功! [TileCount: {intlTileCount} | WallCount: {intlWallCount}]");
                }
                else
                {
                    Logger.Warn("    [警告] 装配件中未找到目标数据结构类。");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"    [异常] 静态解析发生异常: {ex.Message}");
            }
        }

        static int GetFieldConstantFromIL(TypeDefinition typeDef, string fieldName)
        {
            var field = typeDef.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field == null) throw new Exception($"无法找到对应字段 {fieldName}。");
            if (field.HasConstant) return Convert.ToInt32(field.Constant);

            var cctor = typeDef.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor != null && cctor.Body != null)
            {
                var instructions = cctor.Body.Instructions;
                for (int i = instructions.Count - 1; i >= 0; i--)
                {
                    var inst = instructions[i];
                    if (inst.OpCode == OpCodes.Stsfld && (inst.Operand as FieldReference)?.Name == fieldName)
                    {
                        int prevIndex = i - 1;
                        Instruction? prevInst = prevIndex >= 0 ? instructions[prevIndex] : null;

                        while (prevInst != null && (prevInst.OpCode == OpCodes.Nop || prevInst.OpCode == OpCodes.Conv_U2 || prevInst.OpCode == OpCodes.Conv_I4))
                        {
                            prevIndex--;
                            prevInst = prevIndex >= 0 ? instructions[prevIndex] : null;
                        }

                        if (prevInst == null) continue; // CS8602 边界安全防护

                        if (prevInst.OpCode == OpCodes.Ldc_I4 && prevInst.Operand != null) return (int)prevInst.Operand;
                        if (prevInst.OpCode == OpCodes.Ldc_I4_S && prevInst.Operand != null) return (sbyte)prevInst.Operand;
                        if (prevInst.OpCode.Name.StartsWith("ldc.i4."))
                        {
                            string numStr = prevInst.OpCode.Name.Substring(7);
                            if (numStr == "m1") return -1;
                            if (int.TryParse(numStr, out int val)) return val;
                        }
                    }
                }
            }
            throw new Exception("底层指令匹配失败，无法从 IL 中安全提取常数。");
        }

        static bool[] ReadBools(BinaryReader br, int count)
        {
            bool[] arr = new bool[count];
            byte b = 0, b2 = 128;
            for (int i = 0; i < count; i++)
            {
                if (b2 == 128) { b = br.ReadByte(); b2 = 1; }
                else { b2 <<= 1; }
                arr[i] = (b & b2) == b2;
            }
            return arr;
        }

        static void WriteBools(BinaryWriter bw, int count, bool[] bools)
        {
            byte b = 0, b2 = 128;
            for (int i = 0; i < count; i++)
            {
                if (b2 == 128) { b = 0; b2 = 1; }
                else { b2 <<= 1; }
                if (bools[i]) b |= b2;
                if (b2 == 128) bw.Write(b);
            }
            if (b2 != 128) bw.Write(b);
        }

        static Encoding DetectZipEncoding(string zipPath)
        {
            Encoding fallbackEncoding = Encoding.GetEncoding("GBK");
            try
            {
                Encoding rawEncoding = Encoding.GetEncoding("iso-8859-1");
                using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, rawEncoding);
                bool requiresUtf8 = false;
                
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.Any(c => c > 255)) return Encoding.UTF8;
                    byte[] rawBytes = entry.FullName.Select(c => (byte)c).ToArray();
                    if (rawBytes.Any(b => b > 127))
                    {
                        if (IsValidUtf8(rawBytes)) requiresUtf8 = true;
                        else return fallbackEncoding;
                    }
                }
                if (requiresUtf8)
                {
                    Logger.Verbose("    [编码识别] ZIP 内部路径编码推断为: Unicode (UTF-8)");
                    return Encoding.UTF8;
                }
            }
            catch { }
            Logger.Verbose("    [编码识别] ZIP 内部路径编码推断为: GBK (CP936)");
            return fallbackEncoding;
        }

        static bool IsValidUtf8(byte[] bytes)
        {
            try { var utf8 = new UTF8Encoding(false, true); utf8.GetString(bytes); return true; }
            catch { return false; }
        }

        static byte[] DecompressZlib(byte[] data, int expectedSize)
        {
            using MemoryStream msIn = new MemoryStream(data);
            msIn.Seek(2, SeekOrigin.Begin);
            using DeflateStream deflate = new DeflateStream(msIn, CompressionMode.Decompress);
            byte[] outBuf = new byte[expectedSize];
            int totalRead = 0;
            while (totalRead < expectedSize)
            {
                int bytesRead = deflate.Read(outBuf, totalRead, expectedSize - totalRead);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }
            return outBuf;
        }

        static byte[] CompressZlib(byte[] data)
        {
            using MemoryStream outMs = new MemoryStream();
            outMs.WriteByte(0x78); outMs.WriteByte(0x9C);
            using (DeflateStream deflate = new DeflateStream(outMs, CompressionLevel.Optimal, true))
            {
                deflate.Write(data, 0, data.Length);
            }

            uint adler = CalculateAdler32(data);
            outMs.WriteByte((byte)(adler >> 24));
            outMs.WriteByte((byte)(adler >> 16));
            outMs.WriteByte((byte)(adler >> 8));
            outMs.WriteByte((byte)adler);
            return outMs.ToArray();
        }

        static uint CalculateAdler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte val in data)
            {
                a = (a + val) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static byte[] DecryptAES(byte[] input)
        {
            int paddedLength = (int)Math.Ceiling(input.Length / 16.0) * 16;
            byte[] padded = new byte[paddedLength];
            Array.Copy(input, padded, input.Length);
            using Aes aes = Aes.Create();
            aes.Key = ENCRYPTION_KEY; aes.IV = ENCRYPTION_KEY;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(padded, 0, padded.Length);
        }

        static byte[] EncryptAES(byte[] input)
        {
            int paddedLength = (int)Math.Ceiling(input.Length / 16.0) * 16;
            byte[] padded = new byte[paddedLength];
            Array.Copy(input, padded, input.Length);
            using Aes aes = Aes.Create();
            aes.Key = ENCRYPTION_KEY; aes.IV = ENCRYPTION_KEY;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(padded, 0, padded.Length);
        }
    }
}