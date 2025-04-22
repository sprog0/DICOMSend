using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network.Client;
using System.Linq;

namespace DicomConverter
{
    public class DicomProcessor
    {
        private readonly string _pacsServerIp;
        private readonly int _pacsServerPort;
        private readonly string _localAeTitle;
        private readonly string _remoteAeTitle;
        private readonly DicomTransferSyntax? _compression;
        private readonly bool _deleteAfterSend;
        private readonly bool _reEncodeText;

        private static readonly Encoding SourceEncoding = Encoding.GetEncoding(1251);
        private static readonly Encoding TargetEncoding = Encoding.UTF8;

        public DicomProcessor(string pacsServerIp, int pacsServerPort, string localAeTitle, string remoteAeTitle,
                     DicomTransferSyntax? compression, bool deleteAfterSend, bool reEncodeText)
        {
            _pacsServerIp = pacsServerIp;
            _pacsServerPort = pacsServerPort;
            _localAeTitle = localAeTitle;
            _remoteAeTitle = remoteAeTitle;
            _compression = compression;
            _deleteAfterSend = deleteAfterSend;
            _reEncodeText = reEncodeText;
        }

        public async Task ProcessFolderAsync(string folderPath)
        {
            var files = Directory.GetFiles(folderPath, "*.dcm", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Console.WriteLine($"No DICOM files found in {folderPath}");
                return;
            }

            Console.WriteLine($"Found {files.Length} DICOM files to process");

            int successCount = 0;
            foreach (var filePath in files)
            {
                try
                {
                    Console.WriteLine($"Processing {Path.GetFileName(filePath)}...");
                    bool success = await ProcessSingleFileAsync(filePath);
                    if (success)
                    {
                        successCount++;
                        Console.WriteLine("Successfully processed");
                    }
                    else
                    {
                        Console.WriteLine("Failed to process");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing {filePath}: {ex.Message}");
                }
            }

            Console.WriteLine($"Processing complete. Successfully processed {successCount} of {files.Length} files");
        }

        private async Task<bool> ProcessSingleFileAsync(string filePath)
        {
            try
            {
                var originalFile = await DicomFile.OpenAsync(filePath);
                var processedFile = ConvertDicomFile(originalFile);
                var sendSuccess = await SendToPacsAsync(processedFile);

                if (sendSuccess && _deleteAfterSend)
                {
                    File.Delete(filePath);
                }

                return sendSuccess;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file: {ex.Message}");
                return false;
            }
        }

        private DicomFile ConvertDicomFile(DicomFile originalFile)
        {
            DicomFile processedFile = originalFile;
            if (_compression != null)
            {
                processedFile = originalFile.Clone(_compression);
                processedFile.FileMetaInfo.AddOrUpdate(DicomTag.TransferSyntaxUID, _compression.UID.UID);
            }
            if (_reEncodeText) ConvertTextEncoding(processedFile.Dataset);
            processedFile.Dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");
            return processedFile;
        }
        private void ConvertTextEncoding(DicomDataset dataset)
        {
            var list = dataset
                .Where(item => item.ValueRepresentation.IsStringEncoded)
                .Cast<DicomElement>()
                .ToList();

            foreach (var item in list)
            {
                try
                {
                    dataset.AddOrUpdate(item.Tag, TargetEncoding.GetString(Encoding.Convert(SourceEncoding, TargetEncoding, item.Buffer.Data)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error converting tag {item.Tag}: {ex.Message}");
                }
            }
        }

        private async Task<bool> SendToPacsAsync(DicomFile file)
        {
            try
            {
                var client = DicomClientFactory.Create(
                    _pacsServerIp,
                    _pacsServerPort,
                    useTls: false,
                    callingAe: _localAeTitle,
                    calledAe: _remoteAeTitle);

                var request = new DicomCStoreRequest(file);
                await client.AddRequestAsync(request);

                var completionSource = new TaskCompletionSource<bool>();
                request.OnResponseReceived += (req, response) =>
                {
                    completionSource.SetResult(response.Status == DicomStatus.Success);
                };

                await client.SendAsync();
                return await completionSource.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Send error: {ex.Message}");
                return false;
            }
        }

        class Program
        {
            static async Task Main(string[] args)
            {
                new DicomSetupBuilder()
                  .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>())
                  .SkipValidation()
                  .Build();

                if (args.Length < 5)
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("For single file: DICOMSend <input.dcm> <pacs_ip> <pacs_port> <local_aet> <remote_aet> [-compress type] [-keep] [-fix1251]");
                    Console.WriteLine("For folder: DICOMSend -folder <path> <pacs_ip> <pacs_port> <local_aet> <remote_aet> [-compress type] [-keep] [-fix1251]");
                    Console.WriteLine("Compression types: jpeg, jpeg2000, jpegls, rle");
                    Console.WriteLine("-keep - do not delete source file");
                    Console.WriteLine("-fix1251 - convert all strings from 1251 to UTF-8");
                    return;
                }

                bool processFolder = args[0] == "-folder";
                int argOffset = processFolder ? 1 : 0;

                string path = args[argOffset];
                string pacsIp = args[argOffset + 1];
                int pacsPort = int.Parse(args[argOffset + 2]);
                string localAet = args[argOffset + 3];
                string remoteAet = args[argOffset + 4];

                DicomTransferSyntax? compression = null;
                bool deleteAfterSend = true;
                bool reEncodeText = false;

                // Parse optional parameters
                for (int i = argOffset + 5; i < args.Length; i++)
                {
                    if (args[i] == "-compress" && i + 1 < args.Length)
                    {
                        compression = args[i + 1].ToLower() switch
                        {
                            "jpeg" => DicomTransferSyntax.JPEGProcess1,
                            "jpeg2000" => DicomTransferSyntax.JPEG2000Lossless,
                            "jpegls" => DicomTransferSyntax.JPEGLSLossless,
                            "rle" => DicomTransferSyntax.RLELossless,
                            _ => null
                        };
                        i++;
                    }
                    else if (args[i] == "-keep")
                    {
                        deleteAfterSend = false;
                    }
                    else if (args[i] == "-fix1251")
                    {
                        reEncodeText = true;
                    }
                }

                var processor = new DicomProcessor(pacsIp, pacsPort, localAet, remoteAet, compression, deleteAfterSend, reEncodeText);

                if (processFolder)
                {
                    await processor.ProcessFolderAsync(path);
                }
                else
                {
                    var success = await processor.ProcessSingleFileAsync(path);
                    Console.WriteLine(success ? "Processing completed successfully" : "Processing failed");
                    Environment.Exit(success ? 0 : 1);
                }
            }
        }
    }
}