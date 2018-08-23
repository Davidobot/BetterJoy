namespace BetterJoyForCemu {
	partial class MainForm {
		/// <summary>
		/// Required designer variable.
		/// </summary>
		private System.ComponentModel.IContainer components = null;

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing) {
			if (disposing && (components != null)) {
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code

		/// <summary>
		/// Required method for Designer support - do not modify
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent() {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.console = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.notifyIcon = new System.Windows.Forms.NotifyIcon(this.components);
            this.contextMenu = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.label2 = new System.Windows.Forms.Label();
            this.passiveScanBox = new System.Windows.Forms.CheckBox();
            this.linkLabel1 = new System.Windows.Forms.LinkLabel();
            this.conCntrls = new System.Windows.Forms.GroupBox();
            this.con4 = new System.Windows.Forms.Button();
            this.con3 = new System.Windows.Forms.Button();
            this.con2 = new System.Windows.Forms.Button();
            this.con1 = new System.Windows.Forms.Button();
            this.loc1 = new System.Windows.Forms.Button();
            this.loc2 = new System.Windows.Forms.Button();
            this.loc3 = new System.Windows.Forms.Button();
            this.loc4 = new System.Windows.Forms.Button();
            this.btnTip = new System.Windows.Forms.ToolTip(this.components);
            this.contextMenu.SuspendLayout();
            this.conCntrls.SuspendLayout();
            this.SuspendLayout();
            // 
            // console
            // 
            this.console.Location = new System.Drawing.Point(12, 132);
            this.console.Multiline = true;
            this.console.Name = "console";
            this.console.ReadOnly = true;
            this.console.Size = new System.Drawing.Size(262, 100);
            this.console.TabIndex = 2;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(9, 116);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(80, 13);
            this.label1.TabIndex = 1;
            this.label1.Text = "Console Output";
            this.label1.TextAlign = System.Drawing.ContentAlignment.TopCenter;
            // 
            // notifyIcon
            // 
            this.notifyIcon.BalloonTipText = "Double click the tray icon to maximise";
            this.notifyIcon.BalloonTipTitle = "BetterJoyForCemu";
            this.notifyIcon.ContextMenuStrip = this.contextMenu;
            this.notifyIcon.Icon = ((System.Drawing.Icon)(resources.GetObject("notifyIcon.Icon")));
            this.notifyIcon.Text = "BetterJoyForCemu";
            this.notifyIcon.Visible = true;
            this.notifyIcon.MouseDoubleClick += new System.Windows.Forms.MouseEventHandler(this.notifyIcon_MouseDoubleClick);
            // 
            // contextMenu
            // 
            this.contextMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.exitToolStripMenuItem});
            this.contextMenu.Name = "contextMenu";
            this.contextMenu.Size = new System.Drawing.Size(93, 26);
            // 
            // exitToolStripMenuItem
            // 
            this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            this.exitToolStripMenuItem.Size = new System.Drawing.Size(92, 22);
            this.exitToolStripMenuItem.Text = "Exit";
            this.exitToolStripMenuItem.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(246, 239);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(28, 13);
            this.label2.TabIndex = 2;
            this.label2.Text = "v5.0";
            // 
            // passiveScanBox
            // 
            this.passiveScanBox.AutoSize = true;
            this.passiveScanBox.Checked = true;
            this.passiveScanBox.CheckState = System.Windows.Forms.CheckState.Checked;
            this.passiveScanBox.Location = new System.Drawing.Point(12, 238);
            this.passiveScanBox.Name = "passiveScanBox";
            this.passiveScanBox.RightToLeft = System.Windows.Forms.RightToLeft.Yes;
            this.passiveScanBox.Size = new System.Drawing.Size(91, 17);
            this.passiveScanBox.TabIndex = 4;
            this.passiveScanBox.Text = "Passive Scan";
            this.passiveScanBox.UseVisualStyleBackColor = true;
            this.passiveScanBox.CheckedChanged += new System.EventHandler(this.passiveScanBox_CheckedChanged);
            // 
            // linkLabel1
            // 
            this.linkLabel1.AutoSize = true;
            this.linkLabel1.Location = new System.Drawing.Point(196, 239);
            this.linkLabel1.Name = "linkLabel1";
            this.linkLabel1.Size = new System.Drawing.Size(42, 13);
            this.linkLabel1.TabIndex = 5;
            this.linkLabel1.TabStop = true;
            this.linkLabel1.Text = "Donate";
            this.linkLabel1.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.linkLabel1_LinkClicked);
            // 
            // conCntrls
            // 
            this.conCntrls.Controls.Add(this.loc4);
            this.conCntrls.Controls.Add(this.loc3);
            this.conCntrls.Controls.Add(this.loc2);
            this.conCntrls.Controls.Add(this.loc1);
            this.conCntrls.Controls.Add(this.con4);
            this.conCntrls.Controls.Add(this.con3);
            this.conCntrls.Controls.Add(this.con2);
            this.conCntrls.Controls.Add(this.con1);
            this.conCntrls.Location = new System.Drawing.Point(12, 12);
            this.conCntrls.Name = "conCntrls";
            this.conCntrls.Size = new System.Drawing.Size(262, 100);
            this.conCntrls.TabIndex = 0;
            this.conCntrls.TabStop = false;
            this.conCntrls.Text = "Connected Controllers";
            // 
            // con4
            // 
            this.con4.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con4.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con4.Enabled = false;
            this.con4.Location = new System.Drawing.Point(198, 19);
            this.con4.Name = "con4";
            this.con4.Size = new System.Drawing.Size(58, 58);
            this.con4.TabIndex = 3;
            this.con4.TabStop = false;
            this.con4.UseVisualStyleBackColor = true;
            // 
            // con3
            // 
            this.con3.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con3.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con3.Enabled = false;
            this.con3.Location = new System.Drawing.Point(134, 19);
            this.con3.Name = "con3";
            this.con3.Size = new System.Drawing.Size(58, 58);
            this.con3.TabIndex = 2;
            this.con3.TabStop = false;
            this.con3.UseVisualStyleBackColor = true;
            // 
            // con2
            // 
            this.con2.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con2.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con2.Enabled = false;
            this.con2.Location = new System.Drawing.Point(70, 19);
            this.con2.Name = "con2";
            this.con2.Size = new System.Drawing.Size(58, 58);
            this.con2.TabIndex = 1;
            this.con2.TabStop = false;
            this.con2.UseVisualStyleBackColor = true;
            // 
            // con1
            // 
            this.con1.BackgroundImage = global::BetterJoyForCemu.Properties.Resources.cross;
            this.con1.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.con1.Enabled = false;
            this.con1.Location = new System.Drawing.Point(6, 19);
            this.con1.Name = "con1";
            this.con1.Size = new System.Drawing.Size(58, 58);
            this.con1.TabIndex = 0;
            this.con1.TabStop = false;
            this.btnTip.SetToolTip(this.con1, "Click on Joycons to join/split them");
            this.con1.UseVisualStyleBackColor = true;
            // 
            // loc1
            // 
            this.loc1.Location = new System.Drawing.Point(6, 80);
            this.loc1.Name = "loc1";
            this.loc1.Size = new System.Drawing.Size(58, 20);
            this.loc1.TabIndex = 4;
            this.loc1.Text = "Locate";
            this.loc1.UseVisualStyleBackColor = true;
            // 
            // loc2
            // 
            this.loc2.Location = new System.Drawing.Point(70, 80);
            this.loc2.Name = "loc2";
            this.loc2.Size = new System.Drawing.Size(58, 20);
            this.loc2.TabIndex = 5;
            this.loc2.Text = "Locate";
            this.loc2.UseVisualStyleBackColor = true;
            // 
            // loc3
            // 
            this.loc3.Location = new System.Drawing.Point(134, 80);
            this.loc3.Name = "loc3";
            this.loc3.Size = new System.Drawing.Size(58, 20);
            this.loc3.TabIndex = 6;
            this.loc3.Text = "Locate";
            this.loc3.UseVisualStyleBackColor = true;
            // 
            // loc4
            // 
            this.loc4.Location = new System.Drawing.Point(198, 80);
            this.loc4.Name = "loc4";
            this.loc4.Size = new System.Drawing.Size(58, 20);
            this.loc4.TabIndex = 7;
            this.loc4.Text = "Locate";
            this.loc4.UseVisualStyleBackColor = true;
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(284, 261);
            this.Controls.Add(this.conCntrls);
            this.Controls.Add(this.linkLabel1);
            this.Controls.Add(this.passiveScanBox);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.console);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.Text = "BetterJoyForCemu";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.MainForm_FormClosing);
            this.Load += new System.EventHandler(this.MainForm_Load);
            this.Resize += new System.EventHandler(this.MainForm_Resize);
            this.contextMenu.ResumeLayout(false);
            this.conCntrls.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

		}

		#endregion

		public System.Windows.Forms.TextBox console;
		private System.Windows.Forms.Label label1;
		private System.Windows.Forms.NotifyIcon notifyIcon;
		private System.Windows.Forms.Label label2;
		private System.Windows.Forms.ContextMenuStrip contextMenu;
		private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
        private System.Windows.Forms.CheckBox passiveScanBox;
        private System.Windows.Forms.LinkLabel linkLabel1;
        private System.Windows.Forms.GroupBox conCntrls;
        private System.Windows.Forms.Button con1;
        private System.Windows.Forms.Button con4;
        private System.Windows.Forms.Button con3;
        private System.Windows.Forms.Button con2;
        private System.Windows.Forms.Button loc4;
        private System.Windows.Forms.Button loc3;
        private System.Windows.Forms.Button loc2;
        private System.Windows.Forms.Button loc1;
        private System.Windows.Forms.ToolTip btnTip;
    }
}