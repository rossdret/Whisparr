using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ImportApprovedEpisodesFixture : CoreTest<ImportApprovedEpisodes>
    {
        private List<ImportDecision> _rejectedDecisions;
        private List<ImportDecision> _approvedDecisions;

        private DownloadClientItem _downloadClientItem;

        [SetUp]
        public void Setup()
        {
            // The shared logger's first initialization can replace the inherited logging configuration.
            InitLogging();

            _rejectedDecisions = new List<ImportDecision>();
            _approvedDecisions = new List<ImportDecision>();

            var outputPath = @"C:\Test\Unsorted\TV\30.Rock.S01E01".AsOsAgnostic();

            var series = Builder<Series>.CreateNew()
                                        .With(e => e.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                        .With(s => s.Path = @"C:\Test\TV\30 Rock".AsOsAgnostic())
                                        .Build();

            var episodes = Builder<Episode>.CreateListOfSize(5)
                                           .Build();

            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.Add(new ImportDecision(new LocalEpisode(), new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")));
            _rejectedDecisions.ForEach(r => r.LocalEpisode.FileEpisodeInfo = new ParsedEpisodeInfo());

            foreach (var episode in episodes)
            {
                _approvedDecisions.Add(new ImportDecision(
                                           new LocalEpisode
                                               {
                                                   Series = series,
                                                   Episodes = new List<Episode> { episode },
                                                   Path = Path.Combine(series.Path, "30 Rock - S01E01 - Pilot.avi"),
                                                   Quality = new QualityModel(Quality.Bluray720p),
                                                   ReleaseGroup = "DRONE"
                                               }));
            }

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<LocalEpisode>(), It.IsAny<bool>()))
                  .Returns(new EpisodeFileMoveResult());

            Mocker.GetMock<IHistoryService>()
                .Setup(x => x.FindByDownloadId(It.IsAny<string>()))
                .Returns(new List<EpisodeHistory>());

            _downloadClientItem = Builder<DownloadClientItem>.CreateNew()
                .With(d => d.OutputPath = new OsPath(outputPath))
                .Build();
        }

        private void GivenNewDownload()
        {
            _approvedDecisions.ForEach(a => a.LocalEpisode.Path = Path.Combine(_downloadClientItem.OutputPath.ToString(), Path.GetFileName(a.LocalEpisode.Path)));
        }

        private void GivenExistingFileOnDisk()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(new List<EpisodeFile>());
        }

        [Test]
        public void should_not_import_any_if_there_are_no_approved_decisions()
        {
            Subject.Import(_rejectedDecisions, false).Where(i => i.Result == ImportResultType.Imported).Should().BeEmpty();

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.IsAny<EpisodeFile>()), Times.Never());
        }

        [Test]
        public void should_import_each_approved()
        {
            GivenExistingFileOnDisk();

            Subject.Import(_approvedDecisions, false).Should().HaveCount(5);
        }

        [Test]
        public void should_only_import_approved()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_rejectedDecisions);
            all.AddRange(_approvedDecisions);

            var result = Subject.Import(all, false);

            result.Should().HaveCount(all.Count);
            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_only_import_each_episode_once()
        {
            GivenExistingFileOnDisk();

            var all = new List<ImportDecision>();
            all.AddRange(_approvedDecisions);
            all.Add(new ImportDecision(_approvedDecisions.First().LocalEpisode));

            var result = Subject.Import(all, false);

            result.Where(i => i.Result == ImportResultType.Imported).Should().HaveCount(_approvedDecisions.Count);
        }

        [Test]
        public void should_move_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false),
                          Times.Once());
        }

        [Test]
        public void should_publish_EpisodeImportedEvent_for_new_downloads()
        {
            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
        }

        [Test]
        public void should_not_rescan_repeated_collisions_with_an_indexed_destination()
        {
            GivenNewDownload();

            var decision = _approvedDecisions.First();
            var relativePath = Path.Combine("Season 1", "Example Series - Episode 1.mkv");
            var destinationPath = Path.Combine(decision.LocalEpisode.Series.Path, relativePath);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), decision.LocalEpisode, It.IsAny<bool>()))
                  .Throws(new DestinationAlreadyExistsException("Destination already exists.", destinationPath, null));

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(decision.LocalEpisode.Series.Id, relativePath))
                  .Returns(new List<EpisodeFile> { new EpisodeFile { Id = 99, RelativePath = relativePath } });

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = Subject.Import(new List<ImportDecision> { decision }, true).Single();

                result.Result.Should().Be(ImportResultType.Skipped);
                result.Errors.Should().Contain("Failed to import episode, Destination already exists.");
            }

            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Push(It.IsAny<RescanSeriesCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());

            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<EpisodeFile>()), Times.Never());
            Mocker.GetMock<IMediaFileService>()
                  .Verify(s => s.GetFilesWithRelativePath(decision.LocalEpisode.Series.Id, relativePath), Times.Exactly(2));
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Never());

            ExceptionVerification.ExpectedWarns(2);
        }

        [Test]
        public void should_rescan_a_collision_with_an_unindexed_destination()
        {
            GivenNewDownload();
            GivenExistingFileOnDisk();

            var decision = _approvedDecisions.First();
            var destinationPath = Path.Combine(decision.LocalEpisode.Series.Path, "Example Series - Episode 1.mkv");

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), decision.LocalEpisode, It.IsAny<bool>()))
                  .Throws(new DestinationAlreadyExistsException("Destination already exists.", destinationPath, null));

            var result = Subject.Import(new List<ImportDecision> { decision }, true).Single();

            result.Result.Should().Be(ImportResultType.Skipped);
            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Push(It.Is<RescanSeriesCommand>(c => c.SeriesId == decision.LocalEpisode.Series.Id), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_rescan_a_collision_without_a_destination_path()
        {
            GivenNewDownload();

            var decision = _approvedDecisions.First();

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), decision.LocalEpisode, It.IsAny<bool>()))
                  .Throws(new DestinationAlreadyExistsException("Destination already exists."));

            var result = Subject.Import(new List<ImportDecision> { decision }, true).Single();

            result.Result.Should().Be(ImportResultType.Skipped);
            Mocker.GetMock<IMediaFileService>()
                  .Verify(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Push(It.Is<RescanSeriesCommand>(c => c.SeriesId == decision.LocalEpisode.Series.Id), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_rescan_a_collision_outside_the_series_path()
        {
            GivenNewDownload();

            var decision = _approvedDecisions.First();
            var destinationPath = Path.Combine(decision.LocalEpisode.Series.Path + " Other", "Example Series - Episode 1.mkv");

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), decision.LocalEpisode, It.IsAny<bool>()))
                  .Throws(new DestinationAlreadyExistsException("Destination already exists.", destinationPath, null));

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(new List<EpisodeFile> { new EpisodeFile { Id = 99 } });

            var result = Subject.Import(new List<ImportDecision> { decision }, true).Single();

            result.Result.Should().Be(ImportResultType.Skipped);
            Mocker.GetMock<IMediaFileService>()
                  .Verify(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Push(It.Is<RescanSeriesCommand>(c => c.SeriesId == decision.LocalEpisode.Series.Id), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_continue_importing_other_files_after_an_indexed_destination_collision()
        {
            GivenNewDownload();

            var decisions = _approvedDecisions.Take(2).ToList();
            var collision = decisions.First();
            var successful = decisions.Last();
            var relativePath = "Example Series - Episode 1.mkv";
            var destinationPath = Path.Combine(collision.LocalEpisode.Series.Path, relativePath);
            successful.LocalEpisode.Path = Path.Combine(_downloadClientItem.OutputPath.ToString(), "Example Series - Episode 2.mkv");

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), collision.LocalEpisode, It.IsAny<bool>()))
                  .Throws(new DestinationAlreadyExistsException("Destination already exists.", destinationPath, null));

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(collision.LocalEpisode.Series.Id, relativePath))
                  .Returns(new List<EpisodeFile> { new EpisodeFile { Id = 99, RelativePath = relativePath } });
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Add(It.IsAny<EpisodeFile>()))
                  .Returns<EpisodeFile>(file => file);

            var results = Subject.Import(decisions, true);

            results.Should().ContainSingle(r => r.ImportDecision == collision && r.Result == ImportResultType.Skipped);
            results.Should().ContainSingle(r => r.ImportDecision == successful && r.Result == ImportResultType.Imported);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<EpisodeFile>()), Times.Once());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Push(It.IsAny<RescanSeriesCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_continue_importing_other_files_when_destination_lookup_fails()
        {
            GivenNewDownload();

            var decisions = _approvedDecisions.Take(2).ToList();
            var collision = decisions.First();
            var successful = decisions.Last();
            var relativePath = "Example Series - Episode 1.mkv";
            var destinationPath = Path.Combine(collision.LocalEpisode.Series.Path, relativePath);
            successful.LocalEpisode.Path = Path.Combine(_downloadClientItem.OutputPath.ToString(), "Example Series - Episode 2.mkv");

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Setup(s => s.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), collision.LocalEpisode, It.IsAny<bool>()))
                  .Throws(new DestinationAlreadyExistsException("Destination already exists.", destinationPath, null));

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(collision.LocalEpisode.Series.Id, relativePath))
                  .Throws(new IOException("The destination index is temporarily unavailable."));
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Add(It.IsAny<EpisodeFile>()))
                  .Returns<EpisodeFile>(file => file);

            var results = Subject.Import(decisions, true);

            results.Should().ContainSingle(r => r.ImportDecision == collision && r.Result == ImportResultType.Skipped);
            results.Single(r => r.ImportDecision == collision).Errors.Should().Contain("Failed to import episode, Destination already exists.");
            results.Should().ContainSingle(r => r.ImportDecision == successful && r.Result == ImportResultType.Imported);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Add(It.IsAny<EpisodeFile>()), Times.Once());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<EpisodeImportedEvent>()), Times.Once());
            Mocker.GetMock<IManageCommandQueue>()
                  .Verify(s => s.Push(It.Is<RescanSeriesCommand>(c => c.SeriesId == collision.LocalEpisode.Series.Id), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_move_existing_files()
        {
            GivenExistingFileOnDisk();

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false),
                          Times.Never());
        }

        [Test]
        public void should_import_larger_files_first()
        {
            GivenExistingFileOnDisk();

            var fileDecision = _approvedDecisions.First();
            fileDecision.LocalEpisode.Size = 1.Gigabytes();

            var sampleDecision = new ImportDecision(
                new LocalEpisode
                 {
                     Series = fileDecision.LocalEpisode.Series,
                     Episodes = new List<Episode> { fileDecision.LocalEpisode.Episodes.First() },
                     Path = @"C:\Test\TV\30 Rock\30 Rock - S01E01 - Pilot.avi".AsOsAgnostic(),
                     Quality = new QualityModel(Quality.Bluray720p),
                     Size = 80.Megabytes()
                 });

            var all = new List<ImportDecision>();
            all.Add(fileDecision);
            all.Add(sampleDecision);

            var results = Subject.Import(all, false);

            results.Should().HaveCount(all.Count);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported);
            results.Should().ContainSingle(d => d.Result == ImportResultType.Imported && d.ImportDecision.LocalEpisode.Size == fileDecision.LocalEpisode.Size);
        }

        [Test]
        public void should_copy_when_cannot_move_files_downloads()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, true), Times.Once());
        }

        [Test]
        public void should_use_override_importmode()
        {
            GivenNewDownload();
            _downloadClientItem.Title = "30.Rock.S01E01";
            _downloadClientItem.CanMoveFiles = false;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem, ImportMode.Move);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.IsAny<EpisodeFile>(), _approvedDecisions.First().LocalEpisode, false), Times.Once());
        }

        [Test]
        public void should_use_file_name_only_for_download_client_item_without_a_job_folder()
        {
            var fileName = "Series.Title.S01E01.720p.HDTV.x264-Whisparr.mkv";
            var path = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), fileName);

            _downloadClientItem.OutputPath = new OsPath(path);
            _approvedDecisions.First().LocalEpisode.Path = path;

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == fileName)));
        }

        [Test]
        public void should_use_folder_and_file_name_only_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_include_intermediate_folders_for_download_client_item_with_a_job_folder()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);

            _downloadClientItem.OutputPath = new OsPath(outputPath);
            _approvedDecisions.First().LocalEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_windows()
        {
            WindowsOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = @"C:\";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_mono()
        {
            PosixOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = "/";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_get_relative_path_when_there_is_no_grandparent_for_UNC_path()
        {
            WindowsOnly();

            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = @"\\server\share";
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, null);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}.mkv")));
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path_when_file_is_not_in_download_client_item_output_directory()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            _downloadClientItem.OutputPath = new OsPath(Path.Combine(@"C:\Test\Unsorted\TV-Other\".AsOsAgnostic(), name));
            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_delete_existing_metadata_files_with_the_same_path()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesWithRelativePath(It.IsAny<int>(), It.IsAny<string>()))
                  .Returns(Builder<EpisodeFile>.CreateListOfSize(1).BuildList());

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, false);

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Delete(It.IsAny<EpisodeFile>(), DeleteMediaFileReason.ManualOverride), Times.Once());
        }

        [Test]
        public void should_use_folder_info_release_title_to_find_relative_path_when_download_client_item_has_an_empty_output_path()
        {
            var name = "Series.Title.S01E01.720p.HDTV.x264-Whisparr";
            var outputPath = Path.Combine(@"C:\Test\Unsorted\TV\".AsOsAgnostic(), name);
            var localEpisode = _approvedDecisions.First().LocalEpisode;

            _downloadClientItem.OutputPath = default(OsPath);
            localEpisode.FolderEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = name };
            localEpisode.Path = Path.Combine(outputPath, "subfolder", name + ".mkv");

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true, _downloadClientItem);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<EpisodeFile>(c => c.OriginalFilePath == $"{name}\\subfolder\\{name}.mkv".AsOsAgnostic())));
        }

        [Test]
        public void should_include_scene_name_with_new_downloads()
        {
            var firstDecision = _approvedDecisions.First();
            firstDecision.LocalEpisode.SceneName = "Series.Title.S01E01.dvdrip-DRONE";

            Subject.Import(new List<ImportDecision> { _approvedDecisions.First() }, true);

            Mocker.GetMock<IUpgradeMediaFiles>()
                  .Verify(v => v.UpgradeEpisodeFile(It.Is<EpisodeFile>(e => e.SceneName == firstDecision.LocalEpisode.SceneName), _approvedDecisions.First().LocalEpisode, false),
                      Times.Once());
        }
    }
}
