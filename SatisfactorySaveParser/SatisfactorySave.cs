﻿using NLog;
using SatisfactorySaveParser.Exceptions;
using SatisfactorySaveParser.Save;
using SatisfactorySaveParser.Structures;
using SatisfactorySaveParser.ZLib;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SatisfactorySaveParser
{
    /// <summary>
    ///     SatisfactorySave is the main class for parsing a savegame
    /// </summary>
    public class SatisfactorySave
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        /// <summary>
        ///     Path to save on disk
        /// </summary>
        public string FileName { get; private set; }

        /// <summary>
        ///     Header part of the save containing things like the version and metadata
        /// </summary>
        public FSaveHeader Header { get; private set; }

        /// <summary>
        ///     Main content of the save game
        /// </summary>
        public List<SaveObject> Entries { get; set; } = new List<SaveObject>();

        /// <summary>
        ///     List of object references of all collected objects in the world (Nut/berry bushes, slugs, etc)
        /// </summary>
        public List<ObjectReference> CollectedObjects { get; set; } = new List<ObjectReference>();

        /// <summary>
        ///     Open a savefile from disk
        /// </summary>
        /// <param name="file">Full path to the .sav file, usually found in %localappdata%/FactoryGame/Saved/SaveGames</param>
        public SatisfactorySave(string file)
        {
            log.Info($"Opening save file: {file}");

            FileName = Environment.ExpandEnvironmentVariables(file);

            using (var stream = new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream))
            {
                if(stream.Length == 0)
                {
                    throw new Exception("Save file is completely empty");
                }

                Header = FSaveHeader.Parse(reader);

                if (Header.SaveVersion < FSaveCustomVersion.SaveFileIsCompressed)
                {
                    LoadData(reader);
                }
                else
                {
                    using (var buffer = new MemoryStream())
                    {
                        var uncompressedSize = 0L;

                        while (stream.Position < stream.Length)
                        {
                            var header = reader.ReadChunkInfo();
                            Trace.Assert(header.CompressedSize == ChunkInfo.Magic);
                            Trace.Assert(header.UncompressedSize == ChunkInfo.ChunkSize);

                            var summary = reader.ReadChunkInfo();

                            var subChunk = reader.ReadChunkInfo();
                            Trace.Assert(subChunk.UncompressedSize == summary.UncompressedSize);

                            var startPosition = stream.Position;
                            using (var zStream = new ZLibStream(stream, CompressionMode.Decompress))
                            {
                                zStream.CopyTo(buffer);
                            }

                            // ZlibStream appears to read more bytes than it uses (because of buffering probably) so we need to manually fix the input stream position
                            stream.Position = startPosition + subChunk.CompressedSize;

                            uncompressedSize += subChunk.UncompressedSize;
                        }


                        buffer.Position = 0;

#if DEBUG
                        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".bin"), buffer.ToArray());
#endif


                        using (var bufferReader = new BinaryReader(buffer))
                        {
                            var dataLength = bufferReader.ReadInt32();
                            Trace.Assert(uncompressedSize == dataLength + 4);

                            LoadData(bufferReader);
                        }
                    }
                }
            }
        }

        private void LoadData(BinaryReader reader)
        {
            // FIXME currenty ONLY update 6 savefile can be loaded
            if (Header.SaveVersion != FSaveCustomVersion.LatestVersion)
            {
                throw new UnknownBuildVersionException(Header.SaveVersion);
            }
            if (Header.HeaderVersion != SaveHeaderVersion.LatestVersion)
            {
                throw new UnknownSaveVersionException(Header.HeaderVersion);
            }

            var sublevelCount = reader.ReadInt32();
            for (int sublevelIndex = 0; sublevelIndex <= sublevelCount; sublevelIndex++) // sublevels and the "persistent level" at the end!
            {
                var levelName = (sublevelIndex == sublevelCount) ? Header.MapName : reader.ReadLengthPrefixedString(); // use MapName for the persistent level
                var levelEntries = LoadLevelEntries(reader);
                log.Info($"Level '{levelName}' contains {levelEntries.Count} entries");
                Entries.AddRange(levelEntries);

                var levelCollectedObjects = LoadLevelCollectedObjects(reader);
                log.Info($"Level '{levelName}' contains {levelCollectedObjects.Count} collected objects");
                CollectedObjects.AddRange(levelCollectedObjects);

                ParseLevelEntries(levelEntries, reader);
                LoadLevelCollectedObjects(reader); // skip second collectables
            }
        }

        private List<SaveObject> LoadLevelEntries(BinaryReader reader)
        {
            reader.ReadInt32(); // skip "object header and collectables size"
            var levelEntries = new List<SaveObject>();
            var totalSaveObjects = reader.ReadInt32();

            for (int i = 0; i < totalSaveObjects; i++)
            {
                var type = reader.ReadInt32();
                switch (type)
                {
                    case SaveEntity.TypeID:
                        levelEntries.Add(new SaveEntity(reader));
                        break;
                    case SaveComponent.TypeID:
                        levelEntries.Add(new SaveComponent(reader));
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected type {type}");
                }
            }
            return levelEntries;
        }

        private List<ObjectReference> LoadLevelCollectedObjects(BinaryReader reader)
        {
            var levelCollectedObjects = new List<ObjectReference>();
            var collectedObjectsCount = reader.ReadInt32();

            for (int i = 0; i < collectedObjectsCount; i++)
            {
                levelCollectedObjects.Add(new ObjectReference(reader));
            }
            return levelCollectedObjects;
        }

        private void ParseLevelEntries(List<SaveObject> levelEntries, BinaryReader reader)
        {
            reader.ReadInt32(); // skip "objects size"
            var objectCount = reader.ReadInt32();
            Trace.Assert(levelEntries.Count == objectCount);

            for (int i = 0; i < objectCount; i++)
            {
                var len = reader.ReadInt32();
                var before = reader.BaseStream.Position;

#if DEBUG
                //log.Trace($"Reading {len} bytes @ {before} for {levelEntries[i].TypePath}");
#endif

                levelEntries[i].ParseData(len, reader, Header.BuildVersion);
                var after = reader.BaseStream.Position;

                if (before + len != after)
                {
                    throw new InvalidOperationException($"Expected {len} bytes read but got {after - before}");
                }
            }
        }

        public void Save()
        {
            Save(FileName);
        }

        public void Save(string file)
        {
            log.Info($"Writing save file: {file}");

            // FIXME update 6 savefile can be loaded, but not saved yet
            if (Header.SaveVersion > FSaveCustomVersion.TrainBlueprintClassAdded)
            {
                throw new UnknownBuildVersionException(Header.SaveVersion);
            }
            if (Header.HeaderVersion > SaveHeaderVersion.UE426EngineUpdate)
            {
                throw new UnknownSaveVersionException(Header.HeaderVersion);
            }

            FileName = Environment.ExpandEnvironmentVariables(file);
            using (var stream = new FileStream(FileName, FileMode.OpenOrCreate, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                stream.SetLength(0); // Clear any original content

                Header.Serialize(writer);

                if (Header.SaveVersion < FSaveCustomVersion.SaveFileIsCompressed)
                {
                    SaveData(writer, Header.BuildVersion);
                }
                else
                {
                    using (var buffer = new MemoryStream())
                    using (var bufferWriter = new BinaryWriter(buffer))
                    {
                        bufferWriter.Write(0); // Placeholder size

                        SaveData(bufferWriter, Header.BuildVersion);

                        buffer.Position = 0;
                        bufferWriter.Write((int)buffer.Length - 4);
                        buffer.Position = 0;

                        for (var i = 0; i < (int)Math.Ceiling((double)buffer.Length / ChunkInfo.ChunkSize); i++)
                        {
                            using (var zBuffer = new MemoryStream())
                            {
                                var remaining = (int)Math.Min(ChunkInfo.ChunkSize, buffer.Length - (ChunkInfo.ChunkSize * i));

                                using (var zStream = new ZLibStream(zBuffer, CompressionMode.Compress, CompressionLevel.Level6))
                                {
                                    var tmpBuf = new byte[remaining];
                                    buffer.Read(tmpBuf, 0, remaining);
                                    zStream.Write(tmpBuf, 0, remaining);
                                }

                                writer.Write(new ChunkInfo()
                                {
                                    CompressedSize = ChunkInfo.Magic,
                                    UncompressedSize = ChunkInfo.ChunkSize
                                });

                                writer.Write(new ChunkInfo()
                                {
                                    CompressedSize = zBuffer.Length,
                                    UncompressedSize = remaining
                                });

                                writer.Write(new ChunkInfo()
                                {
                                    CompressedSize = zBuffer.Length,
                                    UncompressedSize = remaining
                                });

                                //writer.Write(tmpBuf);
                                //zBuffer.CopyTo(stream);
                                writer.Write(zBuffer.ToArray());
                            }
                        }
                    }
                }
            }
        }

        private void SaveData(BinaryWriter writer, int buildVersion)
        {
            writer.Write(Entries.Count);

            var entities = Entries.Where(e => e is SaveEntity).ToArray();
            for (var i = 0; i < entities.Length; i++)
            {
                writer.Write(SaveEntity.TypeID);
                entities[i].SerializeHeader(writer);
            }

            var components = Entries.Where(e => e is SaveComponent).ToArray();
            for (var i = 0; i < components.Length; i++)
            {
                writer.Write(SaveComponent.TypeID);
                components[i].SerializeHeader(writer);
            }

            writer.Write(entities.Length + components.Length);

            using (var ms = new MemoryStream())
            using (var dataWriter = new BinaryWriter(ms))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    entities[i].SerializeData(dataWriter, buildVersion);

                    var bytes = ms.ToArray();
                    writer.Write(bytes.Length);
                    writer.Write(bytes);

                    ms.SetLength(0);
                }
                for (var i = 0; i < components.Length; i++)
                {
                    components[i].SerializeData(dataWriter, buildVersion);

                    var bytes = ms.ToArray();
                    writer.Write(bytes.Length);
                    writer.Write(bytes);

                    ms.SetLength(0);
                }
            }

            writer.Write(CollectedObjects.Count);
            foreach (var collectedObject in CollectedObjects)
            {
                writer.WriteLengthPrefixedString(collectedObject.LevelName);
                writer.WriteLengthPrefixedString(collectedObject.PathName);
            }
        }
    }
}
