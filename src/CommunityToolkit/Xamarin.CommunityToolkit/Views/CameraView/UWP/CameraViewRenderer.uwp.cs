﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Devices.Sensors;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Xamarin.CommunityToolkit.UI.Views;
using Xamarin.Forms.Platform.UWP;

[assembly: ExportRenderer(typeof(CameraView), typeof(CameraViewRenderer))]

namespace Xamarin.CommunityToolkit.UI.Views
{
	public class CameraViewRenderer : ViewRenderer<CameraView, CaptureElement>
	{
		readonly MediaEncodingProfile encodingProfile;

		MediaCapture? mediaCapture;
		bool isPreviewing;
		Lamp? flash;
		LowLagMediaRecording? mediaRecording;
		string? filePath;
		bool busy;
		VideoStabilizationEffect? videoStabilizationEffect;
		VideoEncodingProperties? inputPropertiesBackup;
		VideoEncodingProperties? outputPropertiesBackup;
		CameraRotationHelper? rotationHelper;

		public CameraViewRenderer() => encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);

		bool IsBusy
		{
			get => busy;
			set
			{
				if (busy != value)
					Element.IsBusy = busy = value;
			}
		}

		bool Available
		{
			get => Element?.IsAvailable ?? false;
			set
			{
				if (Element != null && Element.IsAvailable != value)
					Element.IsAvailable = value;
			}
		}

		protected override async void OnElementChanged(ElementChangedEventArgs<CameraView> e)
		{
			Available = false;
			base.OnElementChanged(e);
			if (e.OldElement != null)
			{
				e.OldElement.ShutterClicked += HandleShutter;
			}
			if (e.NewElement != null)
			{
				if (Control != null && mediaCapture != null)
				{
					await CleanupCameraAsync();
					mediaCapture.Failed -= MediaCaptureFailed;
				}

				SetNativeControl(new CaptureElement());
				_ = Control ?? throw new NullReferenceException();

				Control.HorizontalAlignment = HorizontalAlignment.Stretch;
				Control.VerticalAlignment = VerticalAlignment.Stretch;

				e.NewElement.ShutterClicked += HandleShutter;

				isPreviewing = false;
				await InitializeCameraAsync();

				if (mediaCapture != null)
					mediaCapture.Failed += MediaCaptureFailed;
			}
		}

		async void HandleShutter(object? sender, EventArgs e)
		{
			if (IsBusy)
				return;

			IsBusy = true;
			switch (Element.CaptureMode)
			{
				default:
				case CameraCaptureMode.Default:
				case CameraCaptureMode.Photo:
					if (mediaRecording != null)
						await HandleVideo();
					else
					{
						var tuple = await GetImage();
						if (tuple != null)
							Element.RaiseMediaCaptured(new MediaCapturedEventArgs(tuple.Item1, tuple.Item2, tuple.Item3));
					}
					break;
				case CameraCaptureMode.Video:
					await HandleVideo();
					break;
			}
			IsBusy = false;

			async Task HandleVideo()
			{
				if (mediaRecording == null)
					await StartRecord();
				else
					Element.RaiseMediaCaptured(new MediaCapturedEventArgs(await StopRecord()));
			}
		}

		async Task<Tuple<string?, byte[]?, int>?> GetImage()
		{
			try
			{
				_ = mediaCapture ?? throw new NullReferenceException();

				IsBusy = true;
				var imageProp = ImageEncodingProperties.CreateUncompressed(MediaPixelFormat.Bgra8);
				var lowLagCapture = await mediaCapture.PrepareLowLagPhotoCaptureAsync(imageProp);
				var capturedPhoto = await lowLagCapture.CaptureAsync();

				await lowLagCapture.FinishAsync();
				string? filePath = null;

				// See TODO on CameraView.SavePhotoToFile
				/*if (Element.SavePhotoToFile)
				{
					// TODO replace platform specifics
					// var localFolder = Element.OnThisPlatform().GetPhotoFolder();
					var localFolder = "PhotoFolder";
					var destinationFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(localFolder, CreationCollisionOption.OpenIfExists);
					var file = await destinationFolder.CreateFileAsync($"{DateTime.Now.ToString("yyyyddMM_HHmmss")}.jpg", CreationCollisionOption.GenerateUniqueName);
					filePath = file.Path;
					using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
					{
						var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
						encoder.SetSoftwareBitmap(capturedPhoto.Frame.SoftwareBitmap);
						await encoder.FlushAsync();
					}
				}*/

				// Encode an output stream, it seems you can't use the UWP Frame stream directly
				using var outputStream = new InMemoryRandomAccessStream();
				var outputEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, outputStream);

				var rotationAngleDegrees = 0;

				var orientation = rotationHelper?.GetCameraCaptureOrientation();

				if (Element?.AutoRotatePhoto ?? false)
				{
					if (orientation != SimpleOrientation.NotRotated)
					{
						outputEncoder.BitmapTransform.Rotation = DeviceOrientationToBitmapRotation(orientation);
					}
				}
				else
				{
					rotationAngleDegrees = GetRotationAngleFromDeviceOrientation(orientation);
				}

				outputEncoder.SetSoftwareBitmap(capturedPhoto.Frame.SoftwareBitmap);
				await outputEncoder.FlushAsync();

				// See TODO on CameraView.SavePhotoToFile
				// if (!Element.SavePhotoToFile)
				// {

				using var memoryStream = new MemoryStream();
				await outputStream.AsStream().CopyToAsync(memoryStream);
				var imageData = memoryStream.ToArray();

				// }
				return new Tuple<string?, byte[]?, int>(filePath, imageData, rotationAngleDegrees);
			}
			catch (Exception ex)
			{
				Element?.RaiseMediaCaptureFailed($"Error while taking photo. {ex.Message}");
				return null;
			}
			finally
			{
				IsBusy = false;
			}
		}

		/// <summary>
		/// Returns the rotation angle that an image must be rotated that was taken with a device with
		/// given orientation
		/// </summary>
		/// <param name="orientation">device orientation, or null when none could be determined</param>
		/// <returns>rotation angle, in degrees</returns>
		static int GetRotationAngleFromDeviceOrientation(SimpleOrientation? orientation)
		{
			return orientation switch
			{
				SimpleOrientation.Rotated90DegreesCounterclockwise => 270,
				SimpleOrientation.Rotated180DegreesCounterclockwise => 180,
				SimpleOrientation.Rotated270DegreesCounterclockwise => 90,
				_ => 0,
			};
		}

		// Mirror the device orientation into the bitmap
		static BitmapRotation DeviceOrientationToBitmapRotation(SimpleOrientation? orientation)
		{
			return orientation switch
			{
				SimpleOrientation.NotRotated => BitmapRotation.None,
				SimpleOrientation.Rotated90DegreesCounterclockwise => BitmapRotation.Clockwise270Degrees,
				SimpleOrientation.Rotated180DegreesCounterclockwise => BitmapRotation.Clockwise180Degrees,
				SimpleOrientation.Rotated270DegreesCounterclockwise => BitmapRotation.Clockwise90Degrees,
				_ => BitmapRotation.None,
			};
		}

		async Task StartRecord()
		{
			_ = mediaCapture ?? throw new NullReferenceException();

			// TODO replace platform specifics
			// var localFolder = Element.On<PlatformConfiguration.Windows>().GetVideoFolder();
			var localFolder = "Video";
			var destinationFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(localFolder, CreationCollisionOption.OpenIfExists);
			var file = await destinationFolder.CreateFileAsync($"{DateTime.Now.ToString("yyyyddMM_HHmmss")}.mp4", CreationCollisionOption.GenerateUniqueName);
			filePath = file.Path;
			if (Element.VideoStabilization)
			{
				var stabilizerDefinition = new VideoStabilizationEffectDefinition();
				videoStabilizationEffect = (VideoStabilizationEffect)await mediaCapture.AddVideoEffectAsync(stabilizerDefinition, MediaStreamType.VideoRecord);
				var recommendation = videoStabilizationEffect.GetRecommendedStreamConfiguration(mediaCapture.VideoDeviceController, encodingProfile.Video);
				if (recommendation.InputProperties != null)
				{
					inputPropertiesBackup = mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoRecord) as VideoEncodingProperties;
					await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, recommendation.InputProperties);
					await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, recommendation.InputProperties);
				}
				if (recommendation.OutputProperties != null)
				{
					outputPropertiesBackup = encodingProfile.Video;
					encodingProfile.Video = recommendation.OutputProperties;
				}
				videoStabilizationEffect.Enabled = true;
			}
			mediaRecording = await mediaCapture.PrepareLowLagRecordToStorageFileAsync(encodingProfile, file);
			await mediaRecording.StartAsync();
		}

		async Task<string?> StopRecord()
		{
			if (mediaRecording == null)
				return null;

			await mediaRecording.StopAsync();
			await mediaRecording.FinishAsync();
			mediaRecording = null;

			if (videoStabilizationEffect != null && mediaCapture != null)
			{
				await mediaCapture.RemoveEffectAsync(videoStabilizationEffect);
				videoStabilizationEffect = null;

				if (inputPropertiesBackup != null && mediaCapture != null)
				{
					await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, inputPropertiesBackup);
					inputPropertiesBackup = null;
				}
				if (outputPropertiesBackup != null)
				{
					encodingProfile.Video = outputPropertiesBackup;
					outputPropertiesBackup = null;
				}
				videoStabilizationEffect = null;
			}

			return filePath;
		}

		void MediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
			=> Element?.RaiseMediaCaptureFailed(errorEventArgs.Message);

		protected override async void OnElementPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(CameraView.CameraOptions):
				case nameof(CameraView.CaptureMode):
					await RestartCameraAsync();
					break;
				case nameof(CameraView.FlashMode):
					if (flash != null)
						flash.IsEnabled = Element.FlashMode == CameraFlashMode.Torch || Element.FlashMode == CameraFlashMode.On;
					break;

				// Only supported by Android, removed until we have platform specifics
				// case nameof(CameraView.PreviewAspect):
				// // TODO
				// break;
				case nameof(CameraView.Zoom):
					UpdateZoom();
					break;
			}
			base.OnElementPropertyChanged(sender, e);
		}

		async Task RestartCameraAsync()
		{
			await CleanupCameraAsync();
			await InitializeCameraAsync();
		}

		void UpdateZoom()
		{
			_ = mediaCapture ?? throw new NullReferenceException();
			var zoomControl = mediaCapture.VideoDeviceController.ZoomControl;
			if (!zoomControl.Supported)
				return;

			var newZoomValue = Clamp(Element.Zoom, zoomControl.Min, zoomControl.Max);

			// round down to fixed step value
			newZoomValue -= newZoomValue % zoomControl.Step;

			var settings = new ZoomSettings
			{
				// TODO replace clamp
				Value = newZoomValue,
				Mode = zoomControl.SupportedModes.Contains(ZoomTransitionMode.Direct)
					? ZoomTransitionMode.Direct
					: zoomControl.SupportedModes.First()
			};

			try
			{
				zoomControl.Configure(settings);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"couldn't set zoom factor {newZoomValue}, mode {settings.Mode}: {ex.Message}");
			}

			// Added here since it's an internal method to XF
			static float Clamp(double value, float min, float max)
			{
				if (value.CompareTo(min) < 0)
					return min;
				if (value.CompareTo(max) > 0)
					return max;
				return (float)value;
			}
		}

		DeviceInformation? FilterCamera(DeviceInformationCollection cameraDevices, Windows.Devices.Enumeration.Panel panel)
		{
			foreach (var cam in cameraDevices)
			{
				if (cam.EnclosureLocation?.Panel == panel ||
					(cam.EnclosureLocation == null && panel == Windows.Devices.Enumeration.Panel.Unknown))
					return cam;
			}
			return null;
		}

		async Task InitializeCameraAsync()
		{
			Available = false;
			if (mediaCapture != null)
				return;

			var cameraDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
			if (cameraDevices.Count == 0)
			{
				Element?.RaiseMediaCaptureFailed("Camera devices not found.");
				return;
			}

			IsBusy = true;

			var device = Element.CameraOptions switch
			{
				CameraOptions.Front => FilterCamera(cameraDevices, Windows.Devices.Enumeration.Panel.Front),
				CameraOptions.Back => FilterCamera(cameraDevices, Windows.Devices.Enumeration.Panel.Back),
				CameraOptions.External => FilterCamera(cameraDevices, Windows.Devices.Enumeration.Panel.Unknown),
				_ => cameraDevices?[0],
			};

			if (device == null)
			{
				Element?.RaiseMediaCaptureFailed($"{Element.CameraOptions} camera not found.");
				IsBusy = false;
				return;
			}

			string? selectedAudioDevice = null;
			if (Element.CaptureMode == CameraCaptureMode.Video)
			{
				var audioDevice = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
				selectedAudioDevice = audioDevice.Count == 0 ? null : audioDevice[0].Id;
			}

			mediaCapture = new MediaCapture();
			try
			{
				await mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
				{
					VideoDeviceId = device.Id,
					MediaCategory = MediaCategory.Media,
					StreamingCaptureMode = selectedAudioDevice == null ? StreamingCaptureMode.Video : StreamingCaptureMode.AudioAndVideo,
					AudioProcessing = Windows.Media.AudioProcessing.Default,
					AudioDeviceId = selectedAudioDevice ?? string.Empty,
				});

				// for some reason an event handler for this event must be registered, or the
				// FileLoadException is not thrown in StartPreviewAsync().
				mediaCapture.CaptureDeviceExclusiveControlStatusChanged += CaptureDeviceExclusiveControlStatusChanged;

				flash = await Lamp.GetDefaultAsync();

				if (mediaCapture.VideoDeviceController.ZoomControl.Supported)
					Element.MaxZoom = mediaCapture.VideoDeviceController.ZoomControl.Max;

				DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;

				await SelectHighestVideoPreviewResolution(mediaCapture);
			}
			catch (UnauthorizedAccessException ex)
			{
				Element?.RaiseMediaCaptureFailed($"The app was denied access to the camera or microphone. {ex.Message}");
				IsBusy = false;
				return;
			}
			catch (Exception ex)
			{
				Element?.RaiseMediaCaptureFailed($"Other error while initializing camera. {ex.Message}");
				IsBusy = false;
				return;
			}

			try
			{
				Control.Source = mediaCapture;
				await mediaCapture.StartPreviewAsync();
				await SetUpRotationHelper(device);

				isPreviewing = true;
				Available = true;
			}
			catch (COMException)
			{
				Element?.RaiseMediaCaptureFailed("Camera device is not ready.");
			}
			catch (FileLoadException)
			{
				Element?.RaiseMediaCaptureFailed("The camera is used by another app.");
			}
			catch (Exception ex)
			{
				Element?.RaiseMediaCaptureFailed($"Other error while starting preview. {ex.Message}");
			}
			finally
			{
				IsBusy = false;
			}
		}

		static async Task SelectHighestVideoPreviewResolution(MediaCapture mediaCapture)
		{
			var resolutions = mediaCapture.VideoDeviceController.
				GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
				.ToList();

			resolutions.OrderBy(GetWidthFromMediaEncodingProperties);
			var highestResolution = resolutions.FirstOrDefault();

			if (highestResolution != null)
			{
				await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(
					MediaStreamType.VideoPreview,
					highestResolution);
			}

			static uint GetWidthFromMediaEncodingProperties(IMediaEncodingProperties mediaEncodingProperties)
			{
				return mediaEncodingProperties switch
				{
					VideoEncodingProperties videoEncodingProperties => videoEncodingProperties.Width,
					ImageEncodingProperties imageEncodingProperties => imageEncodingProperties.Width,
					_ => 0
				};
			}
		}

		// Initialize rotation helper
		async Task SetUpRotationHelper(DeviceInformation device)
		{
			var displayInfo = DisplayInformation.GetForCurrentView();
			var simpleOrientationSensor = SimpleOrientationSensor.GetDefault();

			if (displayInfo != null && simpleOrientationSensor != null)
			{
				rotationHelper = new CameraRotationHelper(
					device.EnclosureLocation,
					displayInfo,
					simpleOrientationSensor,
					SetPreviewRotationAsync);

				await SetPreviewRotationAsync();
			}
		}

		async Task CleanupCameraAsync()
		{
			try
			{
				Available = false;
				IsBusy = true;
				if (mediaCapture == null)
					return;

				if (isPreviewing)
				{
					try
					{
						await mediaCapture.StopPreviewAsync();
					}
					catch (Exception)
					{
						// the call may fail when an external camera was already removed
					}

					await Task.Delay(50);
					isPreviewing = false;
				}
				if (mediaRecording != null)
					Element.RaiseMediaCaptured(new MediaCapturedEventArgs(await StopRecord()));

				await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
				{
					Control.Source = null;

					if (mediaCapture != null)
					{
						mediaCapture.CaptureDeviceExclusiveControlStatusChanged -= CaptureDeviceExclusiveControlStatusChanged;
						mediaCapture.Dispose();
						mediaCapture = null;
					}

					rotationHelper?.RemoveEventHandler();
					rotationHelper = null;
				});
			}
			finally
			{
				IsBusy = false;
			}
		}

		protected override async void Dispose(bool disposing)
		{
			await CleanupCameraAsync();
			rotationHelper?.RemoveEventHandler();
			base.Dispose(disposing);
		}

		async void CaptureDeviceExclusiveControlStatusChanged(MediaCapture sender, MediaCaptureDeviceExclusiveControlStatusChangedEventArgs args)
		{
			if (args.Status == MediaCaptureDeviceExclusiveControlStatus.SharedReadOnlyAvailable)
			{
				Element?.RaiseMediaCaptureFailed("The camera preview can't be displayed because another app has exclusive access");
				IsBusy = false;
			}
			else if (args.Status == MediaCaptureDeviceExclusiveControlStatus.ExclusiveControlAvailable && !isPreviewing)
			{
				await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
				{
					await RestartCameraAsync();
				});
			}
		}

		// Apply new rotation property to the stream
		async Task SetPreviewRotationAsync()
		{
			if (mediaCapture?.CameraStreamState == CameraStreamState.Streaming)
			{
				// Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
				var rotation = rotationHelper?.GetCameraPreviewOrientation();
				var props = mediaCapture?.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
				var rotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
				props?.Properties.Add(rotationKey, CameraRotationHelper.ConvertSimpleOrientationToClockwiseDegrees(rotation));
				await mediaCapture?.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
			}
		}
	}
}