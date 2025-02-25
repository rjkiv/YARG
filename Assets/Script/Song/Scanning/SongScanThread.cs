using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using YARG.Serialization;
using YARG.Song.Preparsers;

namespace YARG.Song {
	public class SongScanThread {

		private Thread _thread;

		public volatile int foldersScanned;
		public volatile int songsScanned;
		public volatile int errorsEncountered;

		private int _foldersScanned;
		private int _songsScanned;
		private int _errorsEncountered;

		private readonly Dictionary<string, List<SongEntry>> _songsByCacheFolder;
		private readonly Dictionary<string, List<SongError>> _songErrors;
		private readonly Dictionary<string, SongCache>       _songCaches;
		private readonly List<string>                        _cacheErrors;

		public IReadOnlyList<SongEntry> Songs => _songsByCacheFolder.Values.SelectMany(x => x).ToList();
		public IReadOnlyDictionary<string, List<SongError>> SongErrors => _songErrors;
		public IReadOnlyList<string> CacheErrors => _cacheErrors;

		public SongScanThread(bool fast) {
			_songsByCacheFolder = new Dictionary<string, List<SongEntry>>();
			_songErrors = new Dictionary<string, List<SongError>>();
			_songCaches = new Dictionary<string, SongCache>();
			_cacheErrors = new List<string>();

			_thread = fast ? new Thread(FastScan) : new Thread(FullScan);
		}

		public void AddFolder(string folder) {
			if (IsScanning()) {
				throw new Exception("Cannot add folder while scanning");
			}

			if (_songsByCacheFolder.ContainsKey(folder)) {
				Debug.LogWarning("Two song folders with same directory!");
				return;
			}

			_songsByCacheFolder.Add(folder, new List<SongEntry>());
			_songErrors.Add(folder, new List<SongError>());
			_songCaches.Add(folder, new SongCache(folder));
		}

		public bool IsScanning() {
			return _thread is not null && _thread.IsAlive;
		}

		public void Abort() {
			if (_thread is null || !_thread.IsAlive) {
				return;
			}

			_thread.Abort();
			_thread = null;
		}

		public bool StartScan() {
			if (_thread.IsAlive) {
				Debug.LogError("This scan thread is already in progress!");
				return false;
			}

			if (_songsByCacheFolder.Keys.Count == 0) {
				Debug.LogWarning("No song folders added to this thread");
				return false;
			}

			_thread.Start();
			return true;
		}

		private void FullScan() {
			Debug.Log("Performing full scan");
			foreach (string cache in _songsByCacheFolder.Keys) {
				// Folder doesn't exist, so report as an error and skip
				if (!Directory.Exists(cache)) {
					_songErrors[cache].Add(new SongError(cache, ScanResult.InvalidDirectory));

					Debug.LogError($"Invalid song directory: {cache}");
					continue;
				}

				Debug.Log($"Scanning folder: {cache}");
				ScanSubDirectory(cache, cache, _songsByCacheFolder[cache]);

				Debug.Log($"Finished scanning {cache}, writing cache");

				_songCaches[cache].WriteCache(_songsByCacheFolder[cache]);
				Debug.Log("Wrote cache");
			}
		}

		private void FastScan() {
			Debug.Log("Performing fast scan");

			// This is stupid
			var caches = new Dictionary<string, List<SongEntry>>();
			foreach (string folder in _songsByCacheFolder.Keys) {
				try {
					Debug.Log($"Reading cache of {folder}");
					caches.Add(folder, _songCaches[folder].ReadCache());
					Debug.Log($"Read cache of {folder}");
				} catch (Exception e) {
					_cacheErrors.Add(folder);

					Debug.LogException(e);
					Debug.LogError($"Failed to read cache of {folder}");
				}
			}

			foreach (var cache in caches) {
				_songsByCacheFolder[cache.Key] = cache.Value;
				Debug.Log($"Songs read from {cache.Key}: {cache.Value.Count}");
			}
		}

		private void ScanSubDirectory(string cacheFolder, string subDir, ICollection<SongEntry> songs) {
			_foldersScanned++;
			foldersScanned = _foldersScanned;

			// Check if it is a song folder
			var result = ScanIniSong(cacheFolder, subDir, out var song);
			switch (result) {
				case ScanResult.Ok:
					_songsScanned++;
					songsScanned = _songsScanned;

					songs.Add(song);
					return;
				case ScanResult.NotASong:
					break;
				default:
					_errorsEncountered++;
					errorsEncountered = _errorsEncountered;
					_songErrors[cacheFolder].Add(new SongError(subDir, result));
					Debug.LogWarning($"Error encountered with {subDir}: {result}");
					return;
			}

			// Raw CON folder, so don't scan anymore subdirectories here
			string songsPath = Path.Combine(subDir, "songs");
			if (File.Exists(Path.Combine(songsPath, "songs.dta"))) {
				List<ExtractedConSongEntry> files = ExCONBrowser.BrowseFolder(songsPath);

				foreach (ExtractedConSongEntry file in files) {
					// validate that the song is good to add in-game
					var ExCONResult = ScanExConSong(cacheFolder, file);
					switch (ExCONResult) {
						case ScanResult.Ok:
							_songsScanned++;
							songsScanned = _songsScanned;
							songs.Add(file);
							break;
						case ScanResult.NotASong:
							break;
						default:
							_errorsEncountered++;
							errorsEncountered = _errorsEncountered;
							_songErrors[cacheFolder].Add(new SongError(subDir, ExCONResult));
							Debug.LogWarning($"Error encountered with {subDir}");
							break;
					}
				}

				return;
			}

			// Iterate through the files in this current directory to look for CON files
			foreach (var file in Directory.EnumerateFiles(subDir)) {
				// for each file found, read first 4 bytes and check for "CON " or "LIVE"
				using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
				using var br = new BinaryReader(fs);
				string fHeader = Encoding.UTF8.GetString(br.ReadBytes(4));
				if (fHeader == "CON " || fHeader == "LIVE") {
					List<ConSongEntry> SongsInsideCON = XboxCONFileBrowser.BrowseCON(file);
					// for each CON song that was found (assuming some WERE found)
					if (SongsInsideCON != null) {
						foreach (ConSongEntry SongInsideCON in SongsInsideCON) {
							// validate that the song is good to add in-game
							var CONResult = ScanConSong(cacheFolder, SongInsideCON);
							switch (CONResult) {
								case ScanResult.Ok:
									_songsScanned++;
									songsScanned = _songsScanned;
									songs.Add(SongInsideCON);
									break;
								case ScanResult.NotASong:
									break;
								default:
									_errorsEncountered++;
									errorsEncountered = _errorsEncountered;
									_songErrors[cacheFolder].Add(new SongError(subDir, CONResult));
									Debug.LogWarning($"Error encountered with {subDir}");
									break;
							}
						}
					}
				}
			}

			string[] subdirectories = Directory.GetDirectories(subDir);

			foreach (string subdirectory in subdirectories) {
				ScanSubDirectory(cacheFolder, subdirectory, songs);
			}
		}

		private static ScanResult ScanIniSong(string cache, string directory, out SongEntry song) {
			// Usually we could infer metadata from a .chart file if no ini exists
			// But for now we just skip this song.
			song = null;
			if (!File.Exists(Path.Combine(directory, "song.ini"))) {
				return ScanResult.NotASong;
			}

			if (!File.Exists(Path.Combine(directory, "notes.chart")) &&
				!File.Exists(Path.Combine(directory, "notes.mid"))) {

				return ScanResult.NoNotesFile;
			}

			if (AudioHelpers.GetSupportedStems(directory).Count == 0) {
				return ScanResult.NoAudioFile;
			}

			string notesFile = File.Exists(Path.Combine(directory, "notes.chart")) ? "notes.chart" : "notes.mid";

			// Windows has a 260 character limit for file paths, so we need to check this
			if (Path.Combine(directory, notesFile).Length >= 255) {
				return ScanResult.NoNotesFile;
			}

			byte[] bytes = File.ReadAllBytes(Path.Combine(directory, notesFile));

			string checksum = BitConverter.ToString(SHA1.Create().ComputeHash(bytes)).Replace("-", "");

			var tracks = ulong.MaxValue;

			if (notesFile.EndsWith(".mid")) {
				if (!MidPreparser.GetAvailableTracks(bytes, out ulong availTracks)) {
					return ScanResult.CorruptedNotesFile;
				}
				tracks = availTracks;
			} else if (notesFile.EndsWith(".chart")) {
				if (!ChartPreparser.GetAvailableTracks(bytes, out ulong availTracks)) {
					return ScanResult.CorruptedNotesFile;
				}
				tracks = availTracks;
			}

			// We have a song.ini, notes file and audio. The song is scannable.
			song = new IniSongEntry {
				CacheRoot = cache,
				Location = directory,
				Checksum = checksum,
				NotesFile = notesFile,
				AvailableParts = tracks,
			};

			return ScanHelpers.ParseSongIni(Path.Combine(directory, "song.ini"), (IniSongEntry) song);
		}

		private static ScanResult ScanExConSong(string cache, ExtractedConSongEntry file) {
			// Skip if the song doesn't have notes
			if (!File.Exists(file.NotesFile)) {
				return ScanResult.NoNotesFile;
			}

			// Skip if this is a "fake song" (tutorials, etc.)
			if (file.IsFake) {
				return ScanResult.NotASong;
			}

			// Skip if the mogg is encrypted
			if (file.MoggHeader != 0xA) {
				return ScanResult.EncryptedMogg;
			}

			// all good - go ahead and build the cache info
			byte[] bytes = File.ReadAllBytes(file.NotesFile);

			string checksum = BitConverter.ToString(SHA1.Create().ComputeHash(bytes)).Replace("-", "");

			if (!MidPreparser.GetAvailableTracks(bytes, out ulong tracks)) {
				return ScanResult.CorruptedNotesFile;
			}

			file.CacheRoot = cache;
			file.Checksum = checksum;
			file.AvailableParts = tracks;

			return ScanResult.Ok;
		}

		private static ScanResult ScanConSong(string cache, ConSongEntry file) {
			// Skip if the song doesn't have notes
			if (file.MidiFileSize == 0) {
				return ScanResult.NoNotesFile;
			}

			// Skip if this is a "fake song" (tutorials, etc.)
			if (file.IsFake) {
				return ScanResult.NotASong;
			}

			// Skip if the mogg is encrypted
			if (file.MoggHeader != 0xA) {
				return ScanResult.EncryptedMogg;
			}

			// all good - go ahead and build the cache info

			// construct the midi file
			byte[] bytes = XboxCONInnerFileRetriever.RetrieveFile(file.Location, file.MidiFileSize, file.MidiFileMemBlockOffsets);

			string checksum = BitConverter.ToString(SHA1.Create().ComputeHash(bytes)).Replace("-", "");

			if (!MidPreparser.GetAvailableTracks(bytes, out ulong tracks)) {
				return ScanResult.CorruptedNotesFile;
			}

			file.CacheRoot = cache;
			file.Checksum = checksum;
			file.AvailableParts = tracks;

			return ScanResult.Ok;
		}
	}

}