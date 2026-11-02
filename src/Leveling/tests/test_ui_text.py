from pathlib import Path
import re, unittest
root=Path(__file__).resolve().parents[1]
class UiText(unittest.TestCase):
    def test_display_sources_have_no_corrupt_utf8(self):
        bad=[]
        for folder in ['Views','Services']:
            for p in (root/folder).glob('*'):
                if p.suffix not in {'.xaml','.cs'}: continue
                text=p.read_text(encoding='utf-8-sig')
                if re.search(r'Ã[\u0080-\u00bf]|â[€€“–—]|\ufffd',text): bad.append(str(p.relative_to(root)))
        self.assertEqual(bad,[])
    def test_text_fields_center_content(self):
        text=(root/'App.xaml').read_text(encoding='utf-8-sig')
        for control in ['TextBox','ComboBox']:
            style=text.split(f'<Style TargetType="{control}">')[1].split('</Style>')[0]
            self.assertIn('Property="VerticalContentAlignment" Value="Center"',style)
    def test_hover_keeps_text_at_native_scale(self):
        text=(root/'Services/InterfaceMotion.cs').read_text(encoding='utf-8-sig')
        self.assertNotIn('visual.StartAnimation("Scale"',text)
if __name__=='__main__': unittest.main()
