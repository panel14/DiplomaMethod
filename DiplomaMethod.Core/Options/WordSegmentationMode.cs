namespace DiplomaMethod.Core.Options;

public enum WordSegmentationMode
{
    None,        // Paddle на всю строку, пробелы не вставляются
    WordLevelOcr,   // CCA → кроп каждого слова → Paddle на каждое слово
    Proportional,   // Paddle на всю строку + CCA для границ + пропорциональное распределение символов
    Window          // строка → resize h=48 → нарезка на окна ≤maxWidth по межсловным пробелам → Paddle по окну (как обучающий split_lines.py)
}
