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

    class Program
    {
        private static readonly byte[] ENCRYPTION_KEY = Encoding.Unicode.GetBytes("h3y_gUyZ");
        private const ulong INTL_MAGIC = 0x006369676F6C6572; // "cigoler"
        private const ulong CN_MAGIC   = 0x00676E6F646E6978; // "gnodnix"

        // 动态加载的字典常量
        private static int INTL_TILE_COUNT = 625;
        private static int INTL_WALL_COUNT = 316;

        static void Main(string[] args)
        {
            // 注册扩展编码提供程序（用于支持 GBK、GB2312 等本地系统编码的 ZIP 解压）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            
            // 修复 Emoji 输出变问号的问题
            Console.OutputEncoding = Encoding.UTF8;

            Console.WriteLine("==================================================");
            Console.WriteLine(" 泰拉瑞亚 存档全自动双向 ZIP 转换工具 (增强版)");
            Console.WriteLine("==================================================");

            if (args.Length < 2)
            {
                Console.WriteLine("用法: TerrariaSaveConverter.exe <文件路径或ZIP包> <目标平台(cn/intl)>");
                return;
            }

            // 1. 自适应获取游戏常量，加入高级 IL 解析
            TryLoadConstants(out INTL_TILE_COUNT, out INTL_WALL_COUNT);

            string inputPath = args[0];
            string targetStr = args[1].ToLower();
            Platform targetPlatform = targetStr == "cn" ? Platform.China : Platform.International;

            if (targetPlatform == Platform.Unknown || !File.Exists(inputPath))
            {
                Console.WriteLine($"❌ 目标平台错误或输入路径不存在。");
                return;
            }

            string ext = Path.GetExtension(inputPath).ToLower();
            string outputPath = inputPath.Replace(ext, $"_{targetStr}{ext}");

            try
            {
                if (ext == ".zip") ProcessZipArchive(inputPath, outputPath, targetPlatform);
                else ProcessSingleFile(inputPath, outputPath, targetPlatform);
                
                Console.WriteLine("\n🎉 转换流程全部顺利结束！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n❌ 运行中发生致命错误: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ==========================================
        // ZIP 与 单文件处理流
        // ==========================================
        static void ProcessZipArchive(string inputZipPath, string outputZipPath, Platform targetPlatform)
        {
            Console.WriteLine($"📦 正在加载 ZIP 归档包: {Path.GetFileName(inputZipPath)}");
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            // 自动智能检测 ZIP 内部的路径编码 (兼容手机端 UTF8 和 电脑端 GBK)
            Encoding safeEncoding = DetectZipEncoding(inputZipPath);
            Console.WriteLine($"  -> [编码识别] 检测到 ZIP 内部路径编码为: {safeEncoding.EncodingName}");

            using ZipArchive sourceZip = ZipFile.Open(inputZipPath, ZipArchiveMode.Read, safeEncoding);
            using ZipArchive targetZip = ZipFile.Open(outputZipPath, ZipArchiveMode.Create, Encoding.UTF8);
            
            foreach (ZipArchiveEntry entry in sourceZip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;

                string entryExt = Path.GetExtension(entry.Name).ToLower();
                bool isSaveFile = entryExt == ".plr" || entryExt == ".wld" || entryExt == ".map" || entry.Name.Contains(".bak");

                if (!isSaveFile)
                {
                    Console.WriteLine($"  -> [直接复制] {entry.FullName}");
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

                Console.Write($"  -> [正在处理] {entry.FullName} ");
                byte[] processedBytes = ConvertSavePayload(fileBytes, entry.Name, targetPlatform);

                ZipArchiveEntry targetEntry = targetZip.CreateEntry(entry.FullName);
                using Stream outStream = targetEntry.Open();
                outStream.Write(processedBytes, 0, processedBytes.Length);
            }
        }

        static void ProcessSingleFile(string inputPath, string outputPath, Platform targetPlatform)
        {
            byte[] rawBytes = File.ReadAllBytes(inputPath);
            byte[] processedBytes = ConvertSavePayload(rawBytes, Path.GetFileName(inputPath), targetPlatform);
            File.WriteAllBytes(outputPath, processedBytes);
            Console.WriteLine($"🎉 单个文件转换成功: {Path.GetFileName(outputPath)}");
        }

        // ==========================================
        // 核心转换荷载 (分离 Map 与 其他存档)
        // ==========================================
        static byte[] ConvertSavePayload(byte[] fileBytes, string fileName, Platform targetPlatform)
        {
            string fileNameLower = fileName.ToLower();
            // 修正：只有 .plr 玩家文件被 AES 加密。地图和世界是明文的！
            bool isEncrypted = fileNameLower.EndsWith(".plr") || fileNameLower.EndsWith(".plr.bak");

            byte[] processData = isEncrypted ? DecryptAES(fileBytes) : fileBytes;
            if (processData.Length < 12) return fileBytes;

            using MemoryStream ms = new MemoryStream(processData);
            using BinaryReader br = new BinaryReader(ms);

            int versionRaw = br.ReadInt32();
            int trueVersion = versionRaw & ~0x8000;
            bool hasMetadata = trueVersion >= 135;

            // ===== 1. 地图文件处理 (.map) =====
            if (fileNameLower.EndsWith(".map") || fileNameLower.EndsWith(".map.bak"))
            {
                Platform currentPlatform = Platform.Unknown;

                if (hasMetadata)
                {
                    ulong magic = br.ReadUInt64();
                    ulong baseMagic = magic & 0x00FFFFFFFFFFFFFF;
                    if (baseMagic == INTL_MAGIC) currentPlatform = Platform.International;
                    else if (baseMagic == CN_MAGIC) currentPlatform = Platform.China;
                    br.ReadUInt32(); // Revision
                    br.ReadUInt64(); // isFavorite
                }

                string mapName = br.ReadString();
                int worldId = br.ReadInt32();
                int maxY = br.ReadInt32();
                int maxX = br.ReadInt32();
                short num4 = br.ReadInt16(); // Tile options

                // 低于 135 的地图没有 Magic Number，通过 Tile 总数智能判定来源平台
                if (currentPlatform == Platform.Unknown)
                {
                    currentPlatform = num4 > INTL_TILE_COUNT ? Platform.China : Platform.International;
                }

                if (currentPlatform == targetPlatform)
                {
                    Console.WriteLine(" (跳过: 已经是目标格式)");
                    return fileBytes;
                }

                // 进入小地图深度对齐程序 (支持低于135版本的无Magic写入)
                ms.Seek(0, SeekOrigin.Begin);
                processData = ProcessMapData(ms, currentPlatform, targetPlatform, hasMetadata);
            }
            // ===== 2. 世界和玩家文件处理 (.wld / .plr) =====
            else
            {
                if (!hasMetadata)
                {
                    Console.WriteLine(" (跳过: 版本过低, 不存在 Magic Number)");
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
                    Console.WriteLine(" (跳过: 已经是目标格式或未知格式)");
                    return fileBytes;
                }

                ulong newBaseMagic = targetPlatform == Platform.China ? CN_MAGIC : INTL_MAGIC;
                ulong newMagic = newBaseMagic | ((ulong)fileType << 56);
                
                ms.Seek(4, SeekOrigin.Begin);
                using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    bw.Write(newMagic);
                }
                processData = ms.ToArray();
                Console.WriteLine(" (Magic Number 切换完成)");
            }

            // 重新加密 (如果原本是加密的 .plr)
            if (isEncrypted)
            {
                byte[] encryptedData = EncryptAES(processData);
                Array.Resize(ref encryptedData, fileBytes.Length);
                return encryptedData;
            }

            return processData;
        }

        // ==========================================
        // 地图深度处理 (提取、对齐字典、重压 Chunk)
        // ==========================================
        static byte[] ProcessMapData(MemoryStream mapStream, Platform from, Platform to, bool hasMetadata)
        {
            using BinaryReader br = new BinaryReader(mapStream);
            int versionNum = br.ReadInt32();
            
            byte fileType = 3; // 默认 FileType.Map = 3
            if (hasMetadata)
            {
                ulong magic = br.ReadUInt64();
                fileType = (byte)((magic >> 56) & 0xFF);
                br.ReadUInt32();
                br.ReadUInt64();
            }
            
            br.ReadString(); // Name
            br.ReadInt32();  // WorldId
            int maxY = br.ReadInt32();
            int maxX = br.ReadInt32();

            short num4 = br.ReadInt16(); short num5 = br.ReadInt16();
            short num6 = br.ReadInt16(); short num7 = br.ReadInt16();
            short num8 = br.ReadInt16(); short num9 = br.ReadInt16();
            
            bool[] array3 = ReadBools(br, num4); bool[] array4 = ReadBools(br, num5);

            byte[] array5 = new byte[num4];
            for (int i = 0; i < num4; i++) array5[i] = array3[i] ? br.ReadByte() : (byte)1;

            byte[] array6 = new byte[num5];
            for (int i = 0; i < num5; i++) array6[i] = array4[i] ? br.ReadByte() : (byte)1;
            
            long dataStartOffset = br.BaseStream.Position;

            // 纯天然转换 (国际服 -> 国服 无需改写区块字典，直接返回修改 Header 后的数据)
            if (from == Platform.International && to == Platform.China)
            {
                using MemoryStream quickMs = new MemoryStream();
                quickMs.Write(mapStream.ToArray(), 0, (int)mapStream.Length);
                if (hasMetadata)
                {
                    quickMs.Seek(4, SeekOrigin.Begin);
                    using BinaryWriter quickBw = new BinaryWriter(quickMs, Encoding.UTF8, true);
                    quickBw.Write(CN_MAGIC | ((ulong)fileType << 56));
                }
                Console.WriteLine(" (无损转换: 国际服 -> 国服，已修改标识)");
                return quickMs.ToArray();
            }

            // ================== 有损转换 (国服 -> 国际服) ==================
            // 构建降维安全字典，剔除国服独占溢出物块
            Dictionary<ushort, ushort> dict = new Dictionary<ushort, ushort> { { 0, 0 } };
            ushort srcId = 1, dstId = 1;
            
            for (int i = 0; i < num4; i++) {
                bool isIntl = i < INTL_TILE_COUNT;
                for (int j = 0; j < array5[i]; j++) dict.Add(srcId++, isIntl ? dstId++ : (ushort)0);
            }
            for (int i = 0; i < num5; i++) {
                bool isIntl = i < INTL_WALL_COUNT;
                for (int j = 0; j < array6[i]; j++) dict.Add(srcId++, isIntl ? dstId++ : (ushort)0);
            }
            for (int i = 0; i < num6; i++) dict.Add(srcId++, dstId++);
            for (int i = 0; i < num7; i++) dict.Add(srcId++, dstId++);
            for (int i = 0; i < num8; i++) dict.Add(srcId++, dstId++);
            for (int i = 0; i < num9; i++) dict.Add(srcId++, dstId++);

            using MemoryStream outMs = new MemoryStream();
            using BinaryWriter bw = new BinaryWriter(outMs, Encoding.UTF8, true);
            
            // 写入旧头部
            outMs.Write(mapStream.ToArray(), 0, (int)dataStartOffset);
            
            // 覆盖写入国际服新 Magic Number (如存在)
            if (hasMetadata)
            {
                long pos = outMs.Position;
                outMs.Seek(4, SeekOrigin.Begin);
                bw.Write(INTL_MAGIC | ((ulong)fileType << 56));
                outMs.Seek(pos, SeekOrigin.Begin);
            }

            int num18 = (maxX + 63) / 64;
            int num19 = (maxY + 63) / 64;
            int totalChunks = num18 * num19;
            
            int erasedCount = 0;
            for (int l = 0; l < totalChunks; l++)
            {
                int compSize = br.ReadInt32();
                if (compSize > 0)
                {
                    byte[] compData = br.ReadBytes(compSize);
                    byte[] chunkBuf = DecompressZlib(compData, 4096 * 4);

                    for (int i = 0; i < 4096; i++)
                    {
                        int typeOffset = i * 4;
                        ushort type = BitConverter.ToUInt16(chunkBuf, typeOffset);
                        if (type > 0)
                        {
                            if (!dict.ContainsKey(type) || dict[type] == 0) erasedCount++;
                            type = dict.ContainsKey(type) ? dict[type] : (ushort)0;
                        }
                        byte[] typeBytes = BitConverter.GetBytes(type);
                        chunkBuf[typeOffset] = typeBytes[0];
                        chunkBuf[typeOffset + 1] = typeBytes[1];
                    }

                    byte[] newCompData = CompressZlib(chunkBuf);
                    bw.Write(newCompData.Length);
                    outMs.Write(newCompData, 0, newCompData.Length);
                }
                else
                {
                    bw.Write(0);
                }
            }
            
            Console.WriteLine($" (有损转换: 国服 -> 国际服, 字典重构完成, 擦除像素: {erasedCount})");
            return outMs.ToArray();
        }

        // ==========================================
        // 高级反射：从 .cctor 静态构造函数的 IL 提取 readonly 变量
        // ==========================================
        static void TryLoadConstants(out int tileCount, out int wallCount)
        {
            tileCount = 625; wallCount = 316; // 默认兜底
            Console.WriteLine("\n🔍 正在通过 IL 静态解析引擎核心数据...");

            string? exePath = null;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    {
                        string? steamPath = key?.GetValue("InstallPath")?.ToString();
                        if (!string.IsNullOrEmpty(steamPath))
                        {
                            string tPath = Path.Combine(steamPath, @"steamapps\common\Terraria\Terraria.exe");
                            if (File.Exists(tPath)) exePath = tPath;
                        }
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
            // 兼容 Linux / Mac 的跨平台查找可以按需补充

            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine("⚠️ 未找到 Terraria.exe，将使用内置兜底常数。");
                return;
            }

            Console.WriteLine($"✅ 成功定位游戏引擎: {exePath}");
            
            try
            {
                using ModuleDefinition module = ModuleDefinition.ReadModule(exePath);
                var tileIdType = module.Types.FirstOrDefault(t => t.FullName == "Terraria.ID.TileID");
                var wallIdType = module.Types.FirstOrDefault(t => t.FullName == "Terraria.ID.WallID");

                if (tileIdType != null && wallIdType != null)
                {
                    tileCount = GetFieldConstantFromIL(tileIdType, "Count");
                    wallCount = GetFieldConstantFromIL(wallIdType, "Count");
                    Console.WriteLine($"🎯 IL 底层 Dump 提取成功! [TileCount: {tileCount} | WallCount: {wallCount}]\n");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 静态解析失败 ({ex.Message})，使用兜底常数。\n");
            }
        }

        static int GetFieldConstantFromIL(TypeDefinition typeDef, string fieldName)
        {
            var field = typeDef.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field == null) throw new Exception("Field not found.");

            // 如果是 const (字面量) 直接返回
            if (field.HasConstant) return Convert.ToInt32(field.Constant);

            // 如果是 static readonly，解析它的静态构造函数 (.cctor) 查找汇编指令
            var cctor = typeDef.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor != null)
            {
                // 逆序查找：找到为该属性赋值的指令 stsfld
                for (int i = cctor.Body.Instructions.Count - 1; i >= 0; i--)
                {
                    var inst = cctor.Body.Instructions[i];
                    if (inst.OpCode == OpCodes.Stsfld && (inst.Operand as FieldReference)?.Name == fieldName)
                    {
                        // 找到赋值后，取它上一条指令（压入栈的整数）
                        var prevInst = cctor.Body.Instructions[i - 1];
                        if (prevInst.OpCode == OpCodes.Ldc_I4) return (int)prevInst.Operand;
                        if (prevInst.OpCode == OpCodes.Ldc_I4_S) return (sbyte)prevInst.Operand;
                        
                        // 识别简写的底层指令，如 ldc.i4.0 ~ ldc.i4.8, ldc.i4.m1
                        if (prevInst.OpCode.Name.StartsWith("ldc.i4."))
                        {
                            string numStr = prevInst.OpCode.Name.Substring(7);
                            if (numStr == "m1") return -1;
                            if (int.TryParse(numStr, out int val)) return val;
                        }
                    }
                }
            }
            throw new Exception("Unable to extract constant from IL.");
        }

        // ==========================================
        // 基础工具区
        // ==========================================
        static bool[] ReadBools(BinaryReader br, int count)
        {
            bool[] arr = new bool[count]; byte b = 0, b2 = 128;
            for (int i = 0; i < count; i++) {
                if (b2 == 128) { b = br.ReadByte(); b2 = 1; } else { b2 <<= 1; }
                arr[i] = (b & b2) == b2;
            }
            return arr;
        }

        static byte[] DecompressZlib(byte[] data, int expectedSize)
        {
            using MemoryStream msIn = new MemoryStream(data);
            msIn.Seek(2, SeekOrigin.Begin); 
            using DeflateStream deflate = new DeflateStream(msIn, CompressionMode.Decompress);
            byte[] outBuf = new byte[expectedSize];
            int totalRead = 0;
            while (totalRead < expectedSize) {
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
                deflate.Write(data, 0, data.Length);

            uint adler = CalculateAdler32(data);
            outMs.WriteByte((byte)(adler >> 24)); outMs.WriteByte((byte)(adler >> 16));
            outMs.WriteByte((byte)(adler >> 8)); outMs.WriteByte((byte)adler);
            return outMs.ToArray();
        }

        static uint CalculateAdler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte val in data) { a = (a + val) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        // ==========================================
        // 启发式 ZIP 编码智能检测引擎
        // ==========================================
        static Encoding DetectZipEncoding(string zipPath)
        {
            Encoding fallbackEncoding = Encoding.GetEncoding("GBK");
            try
            {
                // ISO-8859-1 是一种单字节编码，它可以将底层原始字节 1:1 无损映射到 char (0~255) 中
                Encoding rawEncoding = Encoding.GetEncoding("iso-8859-1");
                using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, rawEncoding);
                
                bool requiresUtf8 = false;
                
                foreach (var entry in zip.Entries)
                {
                    // 场景 1: ZIP 内部带有标准 UTF-8 标记位，.NET 已自动完美解析，字符存在越界(>255的汉字)
                    if (entry.FullName.Any(c => c > 255)) return Encoding.UTF8;

                    // 提取原始字节
                    byte[] rawBytes = entry.FullName.Select(c => (byte)c).ToArray();
                    
                    // 场景 2: 未标记的中文路径
                    if (rawBytes.Any(b => b > 127))
                    {
                        if (IsValidUtf8(rawBytes)) 
                        {
                            requiresUtf8 = true;
                        }
                        else 
                        {
                            // 只要有任何一条路径无法用严格 UTF-8 解析，就绝对是 Windows 的 GBK 压缩包
                            return fallbackEncoding; 
                        }
                    }
                }
                
                // 如果所有非 ASCII 字节都符合 UTF-8 规则（例如手机端生成的压缩包），则使用 UTF-8
                if (requiresUtf8) return Encoding.UTF8;
            }
            catch { }
            
            return fallbackEncoding;
        }

        static bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                // throwOnInvalidBytes = true (开启严格模式验证)
                var utf8 = new UTF8Encoding(false, true); 
                utf8.GetString(bytes);
                return true;
            }
            catch { return false; }
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