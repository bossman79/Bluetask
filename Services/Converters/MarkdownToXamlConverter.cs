using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System;
using System.Text.RegularExpressions;
using Windows.UI;

namespace Bluetask.Services.Converters
{
	public static class MarkdownToXamlConverter
	{
		public static RichTextBlock ConvertToRichText(string markdown, string defaultForeground = "#C8C8C8")
		{
			var rtb = new RichTextBlock
			{
				TextWrapping = TextWrapping.Wrap,
				LineHeight = 24,
				FontSize = 14
			};

			if (string.IsNullOrWhiteSpace(markdown))
			{
				return rtb;
			}

			var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
			Paragraph? currentParagraph = null;

			foreach (var line in lines)
			{
				var trimmedLine = line.TrimStart();

				// Empty line - new paragraph
				if (string.IsNullOrWhiteSpace(trimmedLine))
				{
					if (currentParagraph != null && currentParagraph.Inlines.Count > 0)
					{
						rtb.Blocks.Add(currentParagraph);
						currentParagraph = null;
					}
					continue;
				}

				// Heading (### or **)
				if (trimmedLine.StartsWith("###") || trimmedLine.StartsWith("**"))
				{
					if (currentParagraph != null && currentParagraph.Inlines.Count > 0)
					{
						rtb.Blocks.Add(currentParagraph);
					}

					var headerText = trimmedLine.TrimStart('#', '*', ' ').TrimEnd('*', ' ');
					var headerParagraph = new Paragraph
					{
						Margin = new Thickness(0, 8, 0, 4)
					};
					headerParagraph.Inlines.Add(new Run
					{
						Text = headerText,
						FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
						FontSize = 15,
						Foreground = new SolidColorBrush(ColorFromHex("#E0E0E0"))
					});
					rtb.Blocks.Add(headerParagraph);
					currentParagraph = null;
					continue;
				}

				// Bullet point
				if (trimmedLine.StartsWith("- ") || trimmedLine.StartsWith("• "))
				{
					if (currentParagraph != null && currentParagraph.Inlines.Count > 0)
					{
						rtb.Blocks.Add(currentParagraph);
					}

					var bulletText = trimmedLine.Substring(2);
					var bulletParagraph = new Paragraph
					{
						Margin = new Thickness(0, 2, 0, 2),
						TextIndent = -16
					};
					bulletParagraph.Inlines.Add(new Run
					{
						Text = "  • ",
						Foreground = new SolidColorBrush(ColorFromHex("#60A5FA"))
					});
					AddFormattedText(bulletParagraph, bulletText, defaultForeground);
					rtb.Blocks.Add(bulletParagraph);
					currentParagraph = null;
					continue;
				}

				// Numbered list
				var numberedMatch = Regex.Match(trimmedLine, @"^(\d+)\.\s+(.*)");
				if (numberedMatch.Success)
				{
					if (currentParagraph != null && currentParagraph.Inlines.Count > 0)
					{
						rtb.Blocks.Add(currentParagraph);
					}

					var number = numberedMatch.Groups[1].Value;
					var text = numberedMatch.Groups[2].Value;
					var numberedParagraph = new Paragraph
					{
						Margin = new Thickness(0, 4, 0, 4)
					};
					numberedParagraph.Inlines.Add(new Run
					{
						Text = $"{number}. ",
						FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
						Foreground = new SolidColorBrush(ColorFromHex("#10B981"))
					});
					AddFormattedText(numberedParagraph, text, defaultForeground);
					rtb.Blocks.Add(numberedParagraph);
					currentParagraph = null;
					continue;
				}

				// Regular paragraph line
				if (currentParagraph == null)
				{
					currentParagraph = new Paragraph
					{
						Margin = new Thickness(0, 2, 0, 2)
					};
				}
				else
				{
					// Add space between lines in same paragraph
					currentParagraph.Inlines.Add(new Run { Text = " " });
				}

				AddFormattedText(currentParagraph, trimmedLine, defaultForeground);
			}

			// Add final paragraph
			if (currentParagraph != null && currentParagraph.Inlines.Count > 0)
			{
				rtb.Blocks.Add(currentParagraph);
			}

			return rtb;
		}

		private static void AddFormattedText(Paragraph paragraph, string text, string defaultColor)
		{
			var segments = Regex.Split(text, @"(\*\*.*?\*\*|\`.*?\`)");

			foreach (var segment in segments)
			{
				if (string.IsNullOrEmpty(segment)) continue;

				// Bold text
				if (segment.StartsWith("**") && segment.EndsWith("**"))
				{
					var boldText = segment.Trim('*');
					paragraph.Inlines.Add(new Run
					{
						Text = boldText,
						FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
						Foreground = new SolidColorBrush(ColorFromHex("#FFFFFF"))
					});
				}
				// Code/command text
				else if (segment.StartsWith("`") && segment.EndsWith("`"))
				{
					var codeText = segment.Trim('`');
					paragraph.Inlines.Add(new Run
					{
						Text = codeText,
						FontFamily = new FontFamily("Consolas"),
						Foreground = new SolidColorBrush(ColorFromHex("#10B981")),
						FontSize = 13
					});
				}
				// Regular text
				else
				{
					paragraph.Inlines.Add(new Run
					{
						Text = segment,
						Foreground = new SolidColorBrush(ColorFromHex(defaultColor))
					});
				}
			}
		}

		private static Color ColorFromHex(string hex)
		{
			try
			{
				hex = hex.TrimStart('#');
				if (hex.Length == 6)
				{
					return Color.FromArgb(
						255,
						Convert.ToByte(hex.Substring(0, 2), 16),
						Convert.ToByte(hex.Substring(2, 2), 16),
						Convert.ToByte(hex.Substring(4, 2), 16)
					);
				}
			}
			catch { }
			return Color.FromArgb(255, 200, 200, 200);
		}
	}
}


