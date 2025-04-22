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
    /// <summary>
    /// Класс для обработки и отправки DICOM файлов на PACS сервер
    /// </summary>
    public class DicomProcessor
    {
        // Параметры подключения к PACS серверу
        private readonly string _pacsServerIp;
        private readonly int _pacsServerPort;
        private readonly string _localAeTitle;
        private readonly string _remoteAeTitle;

        // Параметры обработки файлов
        private readonly DicomTransferSyntax? _compression;
        private readonly bool _deleteAfterSend;
        private readonly bool _reEncodeText;

        // Кодировки для преобразования текста
        private static readonly Encoding SourceEncoding = Encoding.GetEncoding(1251); // Windows-1251 (кириллица)
        private static readonly Encoding TargetEncoding = Encoding.UTF8; // Целевая кодировка

        /// <summary>
        /// Конструктор DICOM процессора
        /// </summary>
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

        /// <summary>
        /// Обработка всех DICOM файлов в указанной папке (рекурсивно)
        /// </summary>
        public async Task ProcessFolderAsync(string folderPath)
        {
            // Поиск всех DICOM файлов в папке и подпапках
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

        /// <summary>
        /// Обработка одного DICOM файла
        /// </summary>
        private async Task<bool> ProcessSingleFileAsync(string filePath)
        {
            try
            {
                // Открытие DICOM файла
                var originalFile = await DicomFile.OpenAsync(filePath);

                // Конвертация файла (сжатие и преобразование текста)
                var processedFile = ConvertDicomFile(originalFile);

                // Отправка на PACS сервер
                var sendSuccess = await SendToPacsAsync(processedFile);

                // Удаление исходного файла при успешной отправке (если включено)
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

        /// <summary>
        /// Конвертация DICOM файла (сжатие и преобразование текста)
        /// </summary>
        private DicomFile ConvertDicomFile(DicomFile originalFile)
        {
            DicomFile processedFile = originalFile;

            // Применение сжатия изображения (если указано)
            if (_compression != null)
            {
                processedFile = originalFile.Clone(_compression);
                processedFile.FileMetaInfo.AddOrUpdate(DicomTag.TransferSyntaxUID, _compression.UID.UID);
            }

            // Преобразование текстовых полей (если включено)
            if (_reEncodeText)
            {
                ConvertTextEncoding(processedFile.Dataset);

                // Установка кодировки UTF-8 в метаданные
                processedFile.Dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");
            }

            return processedFile;
        }

        /// <summary>
        /// Преобразование текстовых полей из Windows-1251 в UTF-8
        /// </summary>
        private void ConvertTextEncoding(DicomDataset dataset)
        {
            // Получение всех текстовых элементов
            var textElements = dataset
                .Where(item => item.ValueRepresentation.IsStringEncoded)
                .Cast<DicomElement>()
                .ToList();

            foreach (var element in textElements)
            {
                try
                {
                    // Преобразование текста и обновление значения
                    string convertedText = TargetEncoding.GetString(
                        Encoding.Convert(SourceEncoding, TargetEncoding, element.Buffer.Data));

                    dataset.AddOrUpdate(element.Tag, convertedText);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error converting tag {element.Tag}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Отправка DICOM файла на PACS сервер через C-STORE
        /// </summary>
        private async Task<bool> SendToPacsAsync(DicomFile file)
        {
            try
            {
                // Создание DICOM клиента
                var client = DicomClientFactory.Create(
                    _pacsServerIp,
                    _pacsServerPort,
                    useTls: false,
                    callingAe: _localAeTitle,
                    calledAe: _remoteAeTitle);

                // Создание запроса на сохранение
                var request = new DicomCStoreRequest(file);
                await client.AddRequestAsync(request);

                // Ожидание ответа от сервера
                var completionSource = new TaskCompletionSource<bool>();
                request.OnResponseReceived += (req, response) =>
                {
                    completionSource.SetResult(response.Status == DicomStatus.Success);
                };

                // Отправка файла
                await client.SendAsync();
                return await completionSource.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Send error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Главный класс программы
        /// </summary>
        class Program
        {
            /// <summary>
            /// Точка входа в программу
            /// </summary>
            static async Task Main(string[] args)
            {
                // Инициализация DICOM библиотеки
                new DicomSetupBuilder()
                  .RegisterServices(s => s.AddFellowOakDicom().AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>())
                  .SkipValidation()
                  .Build();

                // Проверка аргументов командной строки
                if (args.Length < 5)
                {
                    PrintUsage();
                    return;
                }

                // Парсинг аргументов
                bool processFolder = args[0] == "-folder";
                int argOffset = processFolder ? 1 : 0;

                string path = args[argOffset];
                string pacsIp = args[argOffset + 1];
                int pacsPort = int.Parse(args[argOffset + 2]);
                string localAet = args[argOffset + 3];
                string remoteAet = args[argOffset + 4];

                // Параметры по умолчанию
                DicomTransferSyntax? compression = null;
                bool deleteAfterSend = true;
                bool reEncodeText = false;

                // Обработка опциональных параметров
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

                // Создание процессора
                var processor = new DicomProcessor(pacsIp, pacsPort, localAet, remoteAet,
                    compression, deleteAfterSend, reEncodeText);

                // Запуск обработки
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

            /// <summary>
            /// Вывод справки по использованию программы
            /// </summary>
            private static void PrintUsage()
            {
                Console.WriteLine("Send DICOM files to PACS. Usage:");
                Console.WriteLine("For single file: DICOMSend <input.dcm> <pacs_ip> <pacs_port> <local_aet> <remote_aet> [-compress type] [-keep] [-fix1251]");
                Console.WriteLine("For folder: DICOMSend -folder <path> <pacs_ip> <pacs_port> <local_aet> <remote_aet> [-compress type] [-keep] [-fix1251]");
                Console.WriteLine("Compression types: jpeg, jpeg2000, jpegls, rle");
                Console.WriteLine("keep - do not delete source file");
                Console.WriteLine("fix1251 - convert all strings from 1251 to UTF-8");
                Console.WriteLine("Send all dcm files to PACS. DICOMSend -folder C:\\DICOM 10.10.10.10 104 WORKSTATION PACS -compress jpegls -keep -fix1251");
                Console.WriteLine("Files will be compressed to jpegls to save trffic, text convert from Windows-1251 to UTF-8, source files kept.");
            }
        }
    }
}