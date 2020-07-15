﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Roler.Toolkit.File.Mobi.Compression;
using Roler.Toolkit.File.Mobi.Engine;
using Roler.Toolkit.File.Mobi.Entity;

namespace Roler.Toolkit.File.Mobi
{
    public class MobiReader : IDisposable
    {
        private bool _disposed;
        private readonly Stream _stream;
        private readonly IList<PalmDBRecord> _palmDBRecordList = new List<PalmDBRecord>();

        public MobiReader(Stream stream)
        {
            this._stream = stream;
        }

        #region Methods

        public Mobi Read()
        {
            if (this._disposed)
            {
                throw new ObjectDisposedException("stream");
            }

            var structure = this.ReadStructure();
            var result = new Mobi
            {
                Structure = structure,
                Title = structure.FullName,
            };

            if (structure.ExthHeader != null)
            {
                var exthHeader = structure.ExthHeader;
                result.Creator = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Author)?.Data;
                result.Publisher = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Publisher)?.Data;
                result.Description = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Description)?.Data;
                result.Subject = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Subject)?.Data;
                result.Date = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.PublishingDate)?.Data;
                result.Contributor = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Contributor)?.Data;
                result.Rights = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Rights)?.Data;
                result.Type = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Type)?.Data;
                result.Source = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Source)?.Data;
                result.Language = exthHeader.RecordList.FirstOrDefault(p => p.Type == ExthRecordType.Language)?.Data;
            }

            this.RefreshPalmDBRecordList(structure.PalmDB.RecordInfoList);

            result.Text = this.ReadText(structure);

            return result;
        }

        private void RefreshPalmDBRecordList(IList<PalmDBRecordInfo> palmDBRecordInfoList)
        {
            this._palmDBRecordList.Clear();
            PalmDBRecord lastRecord = null;
            foreach (var palmDBRecordInfo in palmDBRecordInfoList)
            {
                var record = new PalmDBRecord(palmDBRecordInfo);
                if (lastRecord != null)
                {
                    lastRecord.Length = (int)(palmDBRecordInfo.Offset - lastRecord.Info.Offset);
                }
                this._palmDBRecordList.Add(record);
                lastRecord = record;
            }
        }

        #region Structure

        private Structure ReadStructure()
        {
            var result = new Structure();
            var palmDB = PalmDBEngine.Read(this._stream) ?? throw new InvalidDataException("file can not open.");
            result.PalmDB = palmDB;
            if (palmDB.RecordInfoList.Any())
            {
                long firstRecordOffset = palmDB.RecordInfoList.First().Offset;
                result.PalmDOCHeader = PalmDOCHeaderEngine.Read(this._stream, firstRecordOffset) ?? throw new InvalidDataException("invalid file! missing part:PalmDOC Header.");

                if (MobiHeaderEngine.TryRead(this._stream, this._stream.Position, out MobiHeader mobiHeader))
                {
                    result.MobiHeader = mobiHeader;
                }
                else
                {
                    throw new InvalidDataException("invalid file! missing part:MOBI Header.");
                }

                if (ExthHeaderEngine.TryRead(this._stream, this._stream.Position, out ExthHeader exthHeader))
                {
                    result.ExthHeader = exthHeader;
                }

                if (mobiHeader.FullNameLength > 0)
                {
                    long fullNameOffset = firstRecordOffset + mobiHeader.FullNameOffset;
                    this._stream.Seek(fullNameOffset, SeekOrigin.Begin);
                    if (this._stream.TryReadString((int)mobiHeader.FullNameLength, out string fullName))
                    {
                        result.FullName = fullName;
                    }
                }

                if (mobiHeader.INDXRecordOffset != MobiHeaderEngine.UnavailableIndex &&
                    mobiHeader.INDXRecordOffset < palmDB.RecordInfoList.Count &&
                    IndxHeaderEngine.TryRead(this._stream, palmDB.RecordInfoList[(int)mobiHeader.INDXRecordOffset].Offset, out IndxHeader indxHeader))
                {
                    result.IndxHeader = indxHeader;
                }

                if (mobiHeader.FLISRecordOffset != MobiHeaderEngine.UnavailableIndex &&
                    mobiHeader.FLISRecordOffset < palmDB.RecordInfoList.Count &&
                    RecordEngine.TryReadFlisRecord(this._stream, palmDB.RecordInfoList[(int)mobiHeader.FLISRecordOffset].Offset, out FlisRecord flisRecord))
                {
                    result.FlisRecord = flisRecord;
                }

                if (mobiHeader.FCISRecordOffset != MobiHeaderEngine.UnavailableIndex &&
                    mobiHeader.FCISRecordOffset < palmDB.RecordInfoList.Count &&
                    RecordEngine.TryReadFcisRecord(this._stream, palmDB.RecordInfoList[(int)mobiHeader.FCISRecordOffset].Offset, out FcisRecord fcisRecord))
                {
                    result.FcisRecord = fcisRecord;
                }

            }

            return result;
        }

        #endregion

        #region Text

        private string ReadText(Structure structure)
        {
            var decompressedByteList = new List<byte>();
            ICompression compression = null;
            Encoding encoding = Encoding.UTF8;

            switch (structure.PalmDOCHeader.Compression)
            {
                case CompressionType.PalmDOC:
                    {
                        compression = new PalmDocCompression();
                    }
                    break;
                case CompressionType.HUFF_CDIC:
                    {
                        compression = CreateHuffCdicCompression(structure.MobiHeader);
                        encoding = Encoding.ASCII;
                    }
                    break;
                default: break;
            }

            if (compression != null)
            {
                long firstNonTextRecordIndex = this.FindFirstNonTextRecordIndex(structure.MobiHeader);
                for (int i = structure.MobiHeader.FirstContentRecordOffset; i < firstNonTextRecordIndex; i++)
                {
                    var recordBytes = this.ReadPalmDBRecord(this._palmDBRecordList[i]);
                    var decompressedBytes = compression.Decompress(recordBytes);
                    decompressedByteList.AddRange(decompressedBytes);
                }
            }

            return encoding.GetString(decompressedByteList.ToArray());
        }

        private HuffCdicCompression CreateHuffCdicCompression(MobiHeader mobiHeader)
        {
            HuffCdicCompression result = null;
            if (mobiHeader.HuffmanRecordOffset != MobiHeaderEngine.UnavailableIndex &&
                mobiHeader.HuffmanRecordOffset < this._palmDBRecordList.Count)
            {
                var huffBytesData = this.ReadPalmDBRecord(this._palmDBRecordList[(int)mobiHeader.HuffmanRecordOffset]);
                var huffData = new List<byte>(huffBytesData);

                var cdicBytesData = this.ReadPalmDBRecord(this._palmDBRecordList[(int)mobiHeader.HuffmanRecordOffset + 1]);
                var cdicData = new List<byte>(cdicBytesData);

                var huffDicts = new List<IList<byte>>
                {
                    cdicData
                };
                for (int i = 2; i < mobiHeader.HuffmanRecordCount; i++)
                {
                    var recordBytes = this.ReadPalmDBRecord(this._palmDBRecordList[(int)mobiHeader.HuffmanRecordOffset + i]);
                    huffDicts.Add(new List<byte>(recordBytes));
                }

                result = new HuffCdicCompression(huffData, cdicData, huffDicts)
                {
                    ExtraFlags = mobiHeader.ExtraRecordDataFlags
                };
            }
            return result;
        }

        private long FindFirstNonTextRecordIndex(MobiHeader mobiHeader)
        {
            long result;
            if (mobiHeader.FirstNonBookIndex != MobiHeaderEngine.UnavailableIndex &&
                mobiHeader.FirstNonBookIndex < this._palmDBRecordList.Count)
            {
                result = mobiHeader.FirstNonBookIndex;
            }
            else
            {
                result = Math.Min(mobiHeader.LastContentRecordOffset, mobiHeader.INDXRecordOffset);
                result = Math.Min(result, mobiHeader.FLISRecordOffset);
                result = Math.Min(result, mobiHeader.FCISRecordOffset);
                result = Math.Min(result, this._palmDBRecordList.Count);
            }
            return result;
        }

        private byte[] ReadPalmDBRecord(PalmDBRecord palmDBRecord)
        {
            byte[] result = new byte[palmDBRecord.Length];
            this._stream.Seek(palmDBRecord.Info.Offset, SeekOrigin.Begin);
            this._stream.Read(result, 0, result.Length);
            return result;
        }

        #endregion

        #region Disposable

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
            }

            _disposed = true;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #endregion
    }
}
