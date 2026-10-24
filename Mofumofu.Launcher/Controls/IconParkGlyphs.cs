using System.Collections.Generic;
using Avalonia.Media;

namespace Mofumofu.Launcher.Controls;

internal enum IconParkPaint
{
    None,
    Accent,
    AccentSoft,
    Text,
    FieldSoft,
}

internal sealed record IconParkShape(
    Geometry Geometry,
    IconParkPaint FillPaint,
    bool HasStroke,
    IconParkPaint StrokePaint);

/// <summary>IconPark 图标路径（由 @icon-park/svg 1.4.2 生成，Apache-2.0）。</summary>
internal static class IconParkGlyphs
{
    public static readonly IReadOnlyDictionary<string, IconParkShape[]> ByKey =
        new Dictionary<string, IconParkShape[]>
        {
            ["IconAccount"] =
            [
            new IconParkShape(Geometry.Parse("M16,12.0 a8,8 0 1,0 16,0 a8,8 0 1,0 -16,0"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M42 44C42 34.0589 33.9411 26 24 26C14.0589 26 6 34.0589 6 44"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconArrowLeft"] =
            [
            new IconParkShape(Geometry.Parse("M31 36L19 24L31 12"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconArrowRight"] =
            [
            new IconParkShape(Geometry.Parse("M19 12L31 24L19 36"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconCheck"] =
            [
            new IconParkShape(Geometry.Parse("M43 11L16.875 37L5 25.1818"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconChevronDown"] =
            [
            new IconParkShape(Geometry.Parse("M36 18L24 30L12 18"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconClock"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24.0084 12.0001L24.0072 24.0089L32.4866 32.4883"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconClose"] =
            [
            new IconParkShape(Geometry.Parse("M8 8L40 40"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M8 40L40 8"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconCreator"] =
            [
            new IconParkShape(Geometry.Parse("M7 42H43"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M11 26.7199V34H18.3172L39 13.3081L31.6951 6L11 26.7199Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconCube"] =
            [
            new IconParkShape(Geometry.Parse("M15.5 9L7 14V24V34L15.5 39L24 44L32.5001 39L41 34V24V14L32.5001 9L24 4L15.5 9Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M41 14L24 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M7 14L24 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 44V24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 19L32 39"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M41 24L24 34"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 34L7 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 39L16 19"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 9L16 19"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 19L16 9"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconDisk"] =
            [
            new IconParkShape(Geometry.Parse("M41 4H7C5.34315 4 4 5.34315 4 7V41C4 42.6569 5.34315 44 7 44H41C42.6569 44 44 42.6569 44 41V7C44 5.34315 42.6569 4 41 4Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M34 4V22H15V4H34Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M29 11V15"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M11.9968 4H36.9984"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconDownload"] =
            [
            new IconParkShape(Geometry.Parse("M6 24.0083V42H42V24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M33 23L24 32L15 23"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M23.9917 6V32"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentArrowClockwise"] =
            [
            new IconParkShape(Geometry.Parse("M42 8V24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 24L6 40"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M42 24C42 14.0589 33.9411 6 24 6C18.9145 6 14.3216 8.10896 11.0481 11.5M6 24C6 33.9411 14.0589 42 24 42C28.8556 42 33.2622 40.0774 36.5 36.9519"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentArrowCounterclockwise"] =
            [
            new IconParkShape(Geometry.Parse("M11.2721 36.7279C14.5294 39.9853 19.0294 42 24 42C33.9411 42 42 33.9411 42 24C42 14.0589 33.9411 6 24 6C19.0294 6 14.5294 8.01472 11.2721 11.2721C9.61407 12.9301 6 17 6 17"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 9V17H14"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentBox"] =
            [
            new IconParkShape(Geometry.Parse("M8,12.0 H40 A2,2 0 0,1 42,14 V40 A2,2 0 0,1 40,42 H8 A2,2 0 0,1 6,40 V14 A2,2 0 0,1 8,12 Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M17.9497 24.0083L29.9497 24.0083"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M6 13L13 5H35L42 13"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentCalendar"] =
            [
            new IconParkShape(Geometry.Parse("M5 19H43V40C43 41.1046 42.1046 42 41 42H7C5.89543 42 5 41.1046 5 40V19Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M5 9C5 7.89543 5.89543 7 7 7H41C42.1046 7 43 7.89543 43 9V19H5V9Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 4V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 4V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M28 34H34"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M14 34H20"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M28 26H34"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M14 26H20"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentChat"] =
            [
            new IconParkShape(Geometry.Parse("M44.0001 24C44.0001 35.0457 35.0458 44 24.0001 44C18.0266 44 4.00006 44 4.00006 44C4.00006 44 4.00006 29.0722 4.00006 24C4.00006 12.9543 12.9544 4 24.0001 4C35.0458 4 44.0001 12.9543 44.0001 24Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M14 18L32 18"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M14 26H32"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M14 34H24"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentCheck"] =
            [
            new IconParkShape(Geometry.Parse("M43 11L16.875 37L5 25.1818"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentClock"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24.0084 12.0001L24.0072 24.0089L32.4866 32.4883"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentCode"] =
            [
            new IconParkShape(Geometry.Parse("M16 13L4 25.4322L16 37"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 13L44 25.4322L32 37"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M28 4L21 44"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentCoffee"] =
            [
            new IconParkShape(Geometry.Parse("M8 42L37 42C38.6569 42 40 40.6569 40 39L40 9C40 7.34315 38.6569 6 37 6L17 6"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M22 36C27.5228 36 32 31.5228 32 26H12C12 31.5228 16.4772 36 22 36Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 18V20"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M22 18V20"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M28 18V20"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentCube"] =
            [
            new IconParkShape(Geometry.Parse("M15.5 9L7 14V24V34L15.5 39L24 44L32.5001 39L41 34V24V14L32.5001 9L24 4L15.5 9Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M41 14L24 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M7 14L24 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 44V24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 19L32 39"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M41 24L24 34"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 34L7 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 39L16 19"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 9L16 19"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 19L16 9"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentDesktop"] =
            [
            new IconParkShape(Geometry.Parse("M19,32 H29 V41 H19 Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M7,8.0 H41 A2,2 0 0,1 43,10 V30 A2,2 0 0,1 41,32 H7 A2,2 0 0,1 5,30 V10 A2,2 0 0,1 7,8 Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M22 27H26"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M14 41L34 41"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentDismiss"] =
            [
            new IconParkShape(Geometry.Parse("M8 8L40 40"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M8 40L40 8"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentDocument"] =
            [
            new IconParkShape(Geometry.Parse("M10 44H38C39.1046 44 40 43.1046 40 42V14H30V4H10C8.89543 4 8 4.89543 8 6V42C8 43.1046 8.89543 44 10 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M30 4L40 14"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 22V36"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M18 22H24L30 22"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentDownload"] =
            [
            new IconParkShape(Geometry.Parse("M6 24.0083V42H42V24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M33 23L24 32L15 23"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M23.9917 6V32"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentFlash"] =
            [
            new IconParkShape(Geometry.Parse("M19 4H37L26 18H41L17 44L22 25H8L19 4Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentFolder"] =
            [
            new IconParkShape(Geometry.Parse("M7 6C7 4.89543 7.89543 4 9 4H39C40.1046 4 41 4.89543 41 6V42C41 43.1046 40.1046 44 39 44H9C7.89543 44 7 43.1046 7 42V6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 29H20"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M16 35H26"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M8 5C8 5 11.7647 18 24 18C36.2353 18 40 5 40 5"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M20,18.0 a4,4 0 1,0 8,0 a4,4 0 1,0 -8,0"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M15 4H9C7.89543 4 7 4.89543 7 6V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M33 4H39C40.1046 4 41 4.89543 41 6V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentFont"] =
            [
            new IconParkShape(Geometry.Parse("M8 10.9333L8 6H40V10.9333"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 6V42"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 42H32"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentGlobe"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M4 24H44"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 44C28.4183 44 32 35.0457 32 24C32 12.9543 28.4183 4 24 4C19.5817 4 16 12.9543 16 24C16 35.0457 19.5817 44 24 44Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M9.85791 10.1421C13.4772 13.7614 18.4772 16 24 16C29.5229 16 34.5229 13.7614 38.1422 10.1421"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M38.1422 37.8579C34.5229 34.2386 29.5229 32 24 32C18.4772 32 13.4772 34.2386 9.85791 37.8579"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentGrid"] =
            [
            new IconParkShape(Geometry.Parse("M18 6H8C6.89543 6 6 6.89543 6 8V18C6 19.1046 6.89543 20 8 20H18C19.1046 20 20 19.1046 20 18V8C20 6.89543 19.1046 6 18 6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M18 28H8C6.89543 28 6 28.8954 6 30V40C6 41.1046 6.89543 42 8 42H18C19.1046 42 20 41.1046 20 40V30C20 28.8954 19.1046 28 18 28Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 6H30C28.8954 6 28 6.89543 28 8V18C28 19.1046 28.8954 20 30 20H40C41.1046 20 42 19.1046 42 18V8C42 6.89543 41.1046 6 40 6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 28H30C28.8954 28 28 28.8954 28 30V40C28 41.1046 28.8954 42 30 42H40C41.1046 42 42 41.1046 42 40V30C42 28.8954 41.1046 28 40 28Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentHeart"] =
            [
            new IconParkShape(Geometry.Parse("M15 8C8.92487 8 4 12.9249 4 19C4 30 17 40 24 42.3262C31 40 44 30 44 19C44 12.9249 39.0751 8 33 8C29.2797 8 25.9907 9.8469 24 12.6738C22.0093 9.8469 18.7203 8 15 8Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentHeartFilled"] =
            [
            new IconParkShape(Geometry.Parse("M15 8C8.92487 8 4 12.9249 4 19C4 30 17 40 24 42.3262C31 40 44 30 44 19C44 12.9249 39.0751 8 33 8C29.2797 8 25.9907 9.8469 24 12.6738C22.0093 9.8469 18.7203 8 15 8Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentHistory"] =
            [
            new IconParkShape(Geometry.Parse("M5.81836 6.72729V14H13.0911"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M4 24C4 35.0457 12.9543 44 24 44V44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C16.598 4 10.1351 8.02111 6.67677 13.9981"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24.005 12L24.0038 24.0088L32.4832 32.4882"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentImage"] =
            [
            new IconParkShape(Geometry.Parse("M39 6H9C7.34315 6 6 7.34315 6 9V39C6 40.6569 7.34315 42 9 42H39C40.6569 42 42 40.6569 42 39V9C42 7.34315 40.6569 6 39 6Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M18 23C20.7614 23 23 20.7614 23 18C23 15.2386 20.7614 13 18 13C15.2386 13 13 15.2386 13 18C13 20.7614 15.2386 23 18 23Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M27.7901 26.2194C28.6064 25.1269 30.2528 25.1538 31.0329 26.2725L39.8077 38.8561C40.7322 40.182 39.7835 42.0001 38.1671 42.0001H16L27.7901 26.2194Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentInfo"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C29.5228 44 34.5228 41.7614 38.1421 38.1421C41.7614 34.5228 44 29.5228 44 24C44 18.4772 41.7614 13.4772 38.1421 9.85786C34.5228 6.23858 29.5228 4 24 4C18.4772 4 13.4772 6.23858 9.85786 9.85786C6.23858 13.4772 4 18.4772 4 24C4 29.5228 6.23858 34.5228 9.85786 38.1421C13.4772 41.7614 18.4772 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 11C25.3807 11 26.5 12.1193 26.5 13.5C26.5 14.8807 25.3807 16 24 16C22.6193 16 21.5 14.8807 21.5 13.5C21.5 12.1193 22.6193 11 24 11Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M24.5 34V20H23.5H22.5"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M21 34H28"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentList"] =
            [
            new IconParkShape(Geometry.Parse("M5 10L8 13L14 7"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M5 24L8 27L14 21"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M5 38L8 41L14 35"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M21 24H43"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M21 38H43"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M21 10H43"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentMusic"] =
            [
            new IconParkShape(Geometry.Parse("M30 34.5C30 32.567 31.567 31 33.5 31H41V34.4C41 36.3882 39.3882 38 37.4 38H33.5C31.567 38 30 36.433 30 34.5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 38.5C6 36.567 7.567 35 9.5 35H16V38.4C16 40.3882 14.3882 42 12.4 42H9.5C7.567 42 6 40.433 6 38.5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 18.044V18.044L41 12.125"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 38V10L41 4V33.6924"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentPaint"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C29.9601 44 26.3359 35.136 30 31C33.1264 27.4709 44 29.0856 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M28 17C29.6569 17 31 15.6569 31 14C31 12.3431 29.6569 11 28 11C26.3431 11 25 12.3431 25 14C25 15.6569 26.3431 17 28 17Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M16 21C17.6569 21 19 19.6569 19 18C19 16.3431 17.6569 15 16 15C14.3431 15 13 16.3431 13 18C13 19.6569 14.3431 21 16 21Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M17 34C18.6569 34 20 32.6569 20 31C20 29.3431 18.6569 28 17 28C15.3431 28 14 29.3431 14 31C14 32.6569 15.3431 34 17 34Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            ],
            ["IconFluentPerson"] =
            [
            new IconParkShape(Geometry.Parse("M16,12.0 a8,8 0 1,0 16,0 a8,8 0 1,0 -16,0"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M42 44C42 34.0589 33.9411 26 24 26C14.0589 26 6 34.0589 6 44"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentPlay"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M20 24V17.0718L26 20.5359L32 24L26 27.4641L20 30.9282V24Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            ],
            ["IconFluentPlug"] =
            [
            new IconParkShape(Geometry.Parse("M6 14H42V24C38 32 32 36 24 36C16 36 10 32 6 24V14Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M33 34L32 44H16L15 34"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M22 24H26"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M16 4L16 12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 4V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentPuzzle"] =
            [
            new IconParkShape(Geometry.Parse("M4 24V12H13V10C13 6.68629 15.6863 4 19 4C22.3137 4 25 6.68629 25 10V12H34V24H38C41.3137 24 44 26.6863 44 30C44 33.3137 41.3137 36 38 36H34V44H4V36H8C11.3137 36 14 33.3137 14 30C14 26.6863 11.3137 24 8 24H4Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentRepeat"] =
            [
            new IconParkShape(Geometry.Parse("M13 35H7V41"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M41 41H35V35"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M35 13H41V7"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M7 7H13V13"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M13 7.29395C7.57778 10.8714 4 17.0178 4 23.9999C4 25.0195 4.0763 26.0213 4.2235 26.9999"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M26.9999 43.7765C26.0213 43.9237 25.0195 44 23.9999 44C17.0178 44 10.8714 40.4222 7.29395 35"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M43.7765 21C43.9237 21.9786 44 22.9804 44 24C44 30.9821 40.4222 37.1285 35 40.706"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M21 4.2235C21.9786 4.0763 22.9804 4 24 4C30.9821 4 37.1285 7.57778 40.706 13"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentRss"] =
            [
            new IconParkShape(Geometry.Parse("M8 44L8 6C8 4.89543 8.89543 4 10 4H38C39.1046 4 40 4.89543 40 6V44L24 35.7273L8 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M23.9497 13.9497L23.9497 25.9497"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M17.9497 19.9497L29.9497 19.9497"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentServer"] =
            [
            new IconParkShape(Geometry.Parse("M44 4H4V20H44V4Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M44 28H4V44H44V28Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M13 10H11C10.4477 10 10 10.4477 10 11V13C10 13.5523 10.4477 14 11 14H13C13.5523 14 14 13.5523 14 13V11C14 10.4477 13.5523 10 13 10Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M13 34H11C10.4477 34 10 34.4477 10 35V37C10 37.5523 10.4477 38 11 38H13C13.5523 38 14 37.5523 14 37V35C14 34.4477 13.5523 34 13 34Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M21 10H19C18.4477 10 18 10.4477 18 11V13C18 13.5523 18.4477 14 19 14H21C21.5523 14 22 13.5523 22 13V11C22 10.4477 21.5523 10 21 10Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M21 34H19C18.4477 34 18 34.4477 18 35V37C18 37.5523 18.4477 38 19 38H21C21.5523 38 22 37.5523 22 37V35C22 34.4477 21.5523 34 21 34Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            ],
            ["IconFluentSettings"] =
            [
            new IconParkShape(Geometry.Parse("M36.686 15.171C37.9364 16.9643 38.8163 19.0352 39.2147 21.2727H44V26.7273H39.2147C38.8163 28.9648 37.9364 31.0357 36.686 32.829L40.0706 36.2137L36.2137 40.0706L32.829 36.686C31.0357 37.9364 28.9648 38.8163 26.7273 39.2147V44H21.2727V39.2147C19.0352 38.8163 16.9643 37.9364 15.171 36.686L11.7863 40.0706L7.92939 36.2137L11.314 32.829C10.0636 31.0357 9.18372 28.9648 8.78533 26.7273H4V21.2727H8.78533C9.18372 19.0352 10.0636 16.9643 11.314 15.171L7.92939 11.7863L11.7863 7.92939L15.171 11.314C16.9643 10.0636 19.0352 9.18372 21.2727 8.78533V4H26.7273V8.78533C28.9648 9.18372 31.0357 10.0636 32.829 11.314L36.2137 7.92939L40.0706 11.7863L36.686 15.171Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 29C26.7614 29 29 26.7614 29 24C29 21.2386 26.7614 19 24 19C21.2386 19 19 21.2386 19 24C19 26.7614 21.2386 29 24 29Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            ],
            ["IconFluentSparkle"] =
            [
            new IconParkShape(Geometry.Parse("M20.1005 8.1005L24.3431 12.3431M30 4V10V4ZM39.8995 8.1005L35.6569 12.3431L39.8995 8.1005ZM44 18H38H44ZM39.8995 27.8995L35.6569 23.6569L39.8995 27.8995ZM30 32V26V32ZM20.1005 27.8995L24.3431 23.6569L20.1005 27.8995ZM16 18H22H16Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M29.5856 18.4143L5.54395 42.4559"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentSpeaker"] =
            [
            new IconParkShape(Geometry.Parse("M24 6V42C17 42 11.7985 32.8391 11.7985 32.8391H6C4.89543 32.8391 4 31.9437 4 30.8391V17.0108C4 15.9062 4.89543 15.0108 6 15.0108H11.7985C11.7985 15.0108 17 6 24 6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32 15L32 15C32.6232 15.5565 33.1881 16.1797 33.6841 16.8588C35.1387 18.8504 36 21.3223 36 24C36 26.6545 35.1535 29.1067 33.7218 31.0893C33.2168 31.7885 32.6391 32.4293 32 33"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M34.2359 41.1857C40.0836 37.6953 44 31.305 44 24C44 16.8085 40.2043 10.5035 34.507 6.97906"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentStar"] =
            [
            new IconParkShape(Geometry.Parse("M23.9986 5L17.8856 17.4776L4 19.4911L14.0589 29.3251L11.6544 43L23.9986 36.4192L36.3454 43L33.9586 29.3251L44 19.4911L30.1913 17.4776L23.9986 5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentStarFilled"] =
            [
            new IconParkShape(Geometry.Parse("M23.9986 5L17.8856 17.4776L4 19.4911L14.0589 29.3251L11.6544 43L23.9986 36.4192L36.3454 43L33.9586 29.3251L44 19.4911L30.1913 17.4776L23.9986 5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFluentSun"] =
            [
            new IconParkShape(Geometry.Parse("M9.15039 9.15088L11.3778 11.3783"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M3 24H6.15"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M9.15039 38.8495L11.3778 36.6221"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M38.8495 38.8495L36.6221 36.6221"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M44.9996 24H41.8496"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M38.8495 9.15088L36.6221 11.3783"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 3V6.15"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 36C30.6274 36 36 30.6274 36 24C36 17.3726 30.6274 12 24 12C17.3726 12 12 17.3726 12 24C12 30.6274 17.3726 36 24 36Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 45.0001V41.8501"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconFluentTarget"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 13L17 34L24 29L31 34L24 13Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            ],
            ["IconFluentTimer"] =
            [
            new IconParkShape(Geometry.Parse("M8,28.0 a16,16 0 1,0 32,0 a16,16 0 1,0 -32,0"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M28 4L20 4"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 4V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M35 16L38 13"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 28V22"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M24 28H18"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconFluentWrench"] =
            [
            new IconParkShape(Geometry.Parse("M44 16C44 22.6274 38.6274 28 32 28C29.9733 28 28.0639 27.4975 26.3896 26.6104L9 44L4 39L21.3896 21.6104C20.5025 19.9361 20 18.0267 20 16C20 9.37258 25.3726 4 32 4C34.0267 4 35.9361 4.50245 37.6104 5.38959L30 13L35 18L42.6104 10.3896C43.4975 12.0639 44 13.9733 44 16Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconFolder"] =
            [
            new IconParkShape(Geometry.Parse("M7 6C7 4.89543 7.89543 4 9 4H39C40.1046 4 41 4.89543 41 6V42C41 43.1046 40.1046 44 39 44H9C7.89543 44 7 43.1046 7 42V6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 29H20"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M16 35H26"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M8 5C8 5 11.7647 18 24 18C36.2353 18 40 5 40 5"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M20,18.0 a4,4 0 1,0 8,0 a4,4 0 1,0 -8,0"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M15 4H9C7.89543 4 7 4.89543 7 6V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M33 4H39C40.1046 4 41 4.89543 41 6V12"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconGlobe"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M4 24H44"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 44C28.4183 44 32 35.0457 32 24C32 12.9543 28.4183 4 24 4C19.5817 4 16 12.9543 16 24C16 35.0457 19.5817 44 24 44Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M9.85791 10.1421C13.4772 13.7614 18.4772 16 24 16C29.5229 16 34.5229 13.7614 38.1422 10.1421"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M38.1422 37.8579C34.5229 34.2386 29.5229 32 24 32C18.4772 32 13.4772 34.2386 9.85791 37.8579"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconGrid"] =
            [
            new IconParkShape(Geometry.Parse("M18 6H8C6.89543 6 6 6.89543 6 8V18C6 19.1046 6.89543 20 8 20H18C19.1046 20 20 19.1046 20 18V8C20 6.89543 19.1046 6 18 6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M18 28H8C6.89543 28 6 28.8954 6 30V40C6 41.1046 6.89543 42 8 42H18C19.1046 42 20 41.1046 20 40V30C20 28.8954 19.1046 28 18 28Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 6H30C28.8954 6 28 6.89543 28 8V18C28 19.1046 28.8954 20 30 20H40C41.1046 20 42 19.1046 42 18V8C42 6.89543 41.1046 6 40 6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 28H30C28.8954 28 28 28.8954 28 30V40C28 41.1046 28.8954 42 30 42H40C41.1046 42 42 41.1046 42 40V30C42 28.8954 41.1046 28 40 28Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconHome"] =
            [
            new IconParkShape(Geometry.Parse("M9 18V42H39V18L24 6L9 18Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M19 29V42H29V29H19Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M9 42H39"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconMaximize"] =
            [
            new IconParkShape(Geometry.Parse("M33 6H42V15"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M42 33V42H33"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M15 42H6V33"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 15V6H15"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconMediaNext"] =
            [
            new IconParkShape(Geometry.Parse("M14 12L26 24L14 36"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M34 12V36"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconMediaPrevious"] =
            [
            new IconParkShape(Geometry.Parse("M34 36L22 24L34 12"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M14 12V36"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconMemory"] =
            [
            new IconParkShape(Geometry.Parse("M8 6V42C8 43.1046 8.89543 44 10 44H38C39.1046 44 40 43.1046 40 42V13.6095C40 13.07 39.782 12.5533 39.3954 12.1768L31.5824 4.56725C31.209 4.20354 30.7083 4 30.187 4H10C8.89543 4 8 4.89543 8 6Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 21L8 21"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 29H30"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M40 36H30"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M30 44L30 21"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M18 44L18 21"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M18 33L8 33"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconMinimize"] =
            [
            new IconParkShape(Geometry.Parse("M10.5 24L38.5 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconMonitor"] =
            [
            new IconParkShape(Geometry.Parse("M4,10 H36 V38 H4 Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M44 14L36 20.75V27.25L44 34V14Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M17 19L23 24L17 29"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconMusic"] =
            [
            new IconParkShape(Geometry.Parse("M30 34.5C30 32.567 31.567 31 33.5 31H41V34.4C41 36.3882 39.3882 38 37.4 38H33.5C31.567 38 30 36.433 30 34.5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 38.5C6 36.567 7.567 35 9.5 35H16V38.4C16 40.3882 14.3882 42 12.4 42H9.5C7.567 42 6 40.433 6 38.5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 18.044V18.044L41 12.125"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M16 38V10L41 4V33.6924"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconNetwork"] =
            [
            new IconParkShape(Geometry.Parse("M39 34L44 39L39 44"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M8 12C10.2091 12 12 10.2091 12 8C12 5.79086 10.2091 4 8 4C5.79086 4 4 5.79086 4 8C4 10.2091 5.79086 12 8 12Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M12 8L20 8C22.2091 8 24 9.79086 24 12V35C24 37.2091 25.7909 39 28 39H44"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconPause"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M19 18V30"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M29 18V30"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
            ["IconPlay"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C35.0457 44 44 35.0457 44 24C44 12.9543 35.0457 4 24 4C12.9543 4 4 12.9543 4 24C4 35.0457 12.9543 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M20 24V17.0718L26 20.5359L32 24L26 27.4641L20 30.9282V24Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            ],
            ["IconPlugin"] =
            [
            new IconParkShape(Geometry.Parse("M4 24V12H13V10C13 6.68629 15.6863 4 19 4C22.3137 4 25 6.68629 25 10V12H34V24H38C41.3137 24 44 26.6863 44 30C44 33.3137 41.3137 36 38 36H34V44H4V36H8C11.3137 36 14 33.3137 14 30C14 26.6863 11.3137 24 8 24H4Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconPlus"] =
            [
            new IconParkShape(Geometry.Parse("M24.0605 10L24.0239 38"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M10 24L38 24"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconRefresh"] =
            [
            new IconParkShape(Geometry.Parse("M42 8V24"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 24L6 40"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M42 24C42 14.0589 33.9411 6 24 6C18.9145 6 14.3216 8.10896 11.0481 11.5M6 24C6 33.9411 14.0589 42 24 42C28.8556 42 33.2622 40.0774 36.5 36.9519"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconRestore"] =
            [
            new IconParkShape(Geometry.Parse("M12.9998 8L6 14L12.9998 21"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M6 14H28.9938C35.8768 14 41.7221 19.6204 41.9904 26.5C42.2739 33.7696 36.2671 40 28.9938 40H11.9984"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconRobot"] =
            [
            new IconParkShape(Geometry.Parse("M11,17.0 H37 A2,2 0 0,1 39,19 V41 A2,2 0 0,1 37,43 H11 A2,2 0 0,1 9,41 V19 A2,2 0 0,1 11,17 Z"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M33 9L28 17"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M15 9L20 17"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M32,7.0 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M12,7.0 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M20,24.0 H28 A4,4 0 0,1 32,28 V28 A4,4 0 0,1 28,32 H20 A4,4 0 0,1 16,28 V28 A4,4 0 0,1 20,24 Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M9 24H4V34H9"), IconParkPaint.None, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M39 24H44V34H39"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconSearch"] =
            [
            new IconParkShape(Geometry.Parse("M21 38C30.3888 38 38 30.3888 38 21C38 11.6112 30.3888 4 21 4C11.6112 4 4 11.6112 4 21C4 30.3888 11.6112 38 21 38Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M26.657 14.3431C25.2093 12.8954 23.2093 12 21.0001 12C18.791 12 16.791 12.8954 15.3433 14.3431"), IconParkPaint.None, true, IconParkPaint.Text),
            new IconParkShape(Geometry.Parse("M33.2216 33.2217L41.7069 41.707"), IconParkPaint.None, true, IconParkPaint.Accent),
            ],
            ["IconServer"] =
            [
            new IconParkShape(Geometry.Parse("M44 4H4V20H44V4Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M44 28H4V44H44V28Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M13 10H11C10.4477 10 10 10.4477 10 11V13C10 13.5523 10.4477 14 11 14H13C13.5523 14 14 13.5523 14 13V11C14 10.4477 13.5523 10 13 10Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M13 34H11C10.4477 34 10 34.4477 10 35V37C10 37.5523 10.4477 38 11 38H13C13.5523 38 14 37.5523 14 37V35C14 34.4477 13.5523 34 13 34Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M21 10H19C18.4477 10 18 10.4477 18 11V13C18 13.5523 18.4477 14 19 14H21C21.5523 14 22 13.5523 22 13V11C22 10.4477 21.5523 10 21 10Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M21 34H19C18.4477 34 18 34.4477 18 35V37C18 37.5523 18.4477 38 19 38H21C21.5523 38 22 37.5523 22 37V35C22 34.4477 21.5523 34 21 34Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            ],
            ["IconSettings"] =
            [
            new IconParkShape(Geometry.Parse("M36.686 15.171C37.9364 16.9643 38.8163 19.0352 39.2147 21.2727H44V26.7273H39.2147C38.8163 28.9648 37.9364 31.0357 36.686 32.829L40.0706 36.2137L36.2137 40.0706L32.829 36.686C31.0357 37.9364 28.9648 38.8163 26.7273 39.2147V44H21.2727V39.2147C19.0352 38.8163 16.9643 37.9364 15.171 36.686L11.7863 40.0706L7.92939 36.2137L11.314 32.829C10.0636 31.0357 9.18372 28.9648 8.78533 26.7273H4V21.2727H8.78533C9.18372 19.0352 10.0636 16.9643 11.314 15.171L7.92939 11.7863L11.7863 7.92939L15.171 11.314C16.9643 10.0636 19.0352 9.18372 21.2727 8.78533V4H26.7273V8.78533C28.9648 9.18372 31.0357 10.0636 32.829 11.314L36.2137 7.92939L40.0706 11.7863L36.686 15.171Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 29C26.7614 29 29 26.7614 29 24C29 21.2386 26.7614 19 24 19C21.2386 19 19 21.2386 19 24C19 26.7614 21.2386 29 24 29Z"), IconParkPaint.FieldSoft, true, IconParkPaint.Text),
            ],
            ["IconStar"] =
            [
            new IconParkShape(Geometry.Parse("M23.9986 5L17.8856 17.4776L4 19.4911L14.0589 29.3251L11.6544 43L23.9986 36.4192L36.3454 43L33.9586 29.3251L44 19.4911L30.1913 17.4776L23.9986 5Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconStop"] =
            [
            new IconParkShape(Geometry.Parse("M39 6H9C7.34315 6 6 7.34315 6 9V39C6 40.6569 7.34315 42 9 42H39C40.6569 42 42 40.6569 42 39V9C42 7.34315 40.6569 6 39 6Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            ],
            ["IconWarning"] =
            [
            new IconParkShape(Geometry.Parse("M24 44C29.5228 44 34.5228 41.7614 38.1421 38.1421C41.7614 34.5228 44 29.5228 44 24C44 18.4772 41.7614 13.4772 38.1421 9.85786C34.5228 6.23858 29.5228 4 24 4C18.4772 4 13.4772 6.23858 9.85786 9.85786C6.23858 13.4772 4 18.4772 4 24C4 29.5228 6.23858 34.5228 9.85786 38.1421C13.4772 41.7614 18.4772 44 24 44Z"), IconParkPaint.AccentSoft, true, IconParkPaint.Accent),
            new IconParkShape(Geometry.Parse("M24 37C25.3807 37 26.5 35.8807 26.5 34.5C26.5 33.1193 25.3807 32 24 32C22.6193 32 21.5 33.1193 21.5 34.5C21.5 35.8807 22.6193 37 24 37Z"), IconParkPaint.AccentSoft, false, IconParkPaint.None),
            new IconParkShape(Geometry.Parse("M24 12V28"), IconParkPaint.None, true, IconParkPaint.Text),
            ],
        };
}
