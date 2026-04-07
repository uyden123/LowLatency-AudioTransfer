using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioTransfer.GUI.Converters
{
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public bool Inverse { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                if (Inverse) b = !b;
                return b ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return value;
        }
    }

    public class InverseCountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = 0;
            if (value is int i) count = i;
            else if (value is ICollection collection) count = collection.Count;

            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts a dB value (-100..0) to a percentage (0.0..1.0) for level meter width.
    /// Used in Discord-style VAD slider.
    /// </summary>
    public class DbToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double db)
            {
                // Clamp to -100..0, then normalize to 0..1
                db = Math.Max(-100, Math.Min(0, db));
                return (db + 100.0) / 100.0;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// MultiValueConverter: takes (dB level, container width) and returns pixel width for level meter.
    /// Used in Discord-style VAD slider to size the colored fill bar.
    /// </summary>
    public class LevelMeterWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is double db && values[1] is double containerWidth)
            {
                db = Math.Max(-100, Math.Min(0, db));
                double pct = (db + 100.0) / 100.0;
                return Math.Max(0, pct * containerWidth);
            }
            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
